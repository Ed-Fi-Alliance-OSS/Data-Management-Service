# DMS-1327: Complete CMS token revocation (Keycloak support and consistent OAuth failure handling)

**Status:** DRAFT, revision 3, approved for P1.1 only (Codex, 2026-10-01). P1.1 is implemented
(tests and this document; no production code) and its evidence is in §9.1, together with the
approved corrections to §2.1, A-06 and D-16 made by the P1.1 correction commit. P2.1 (shared
contract, classified failures, 503 mapping) is implemented as planned in §6, with a correction
commit that adds the failure-preserving secret verification and the read-only certificate loader
described in D-07 and D-13.1. P2.2 (ownership-constrained mutation on both engines, `61e0dda8c`) is
implemented and approved. P2.3 (endpoint request contract, OAuth exception format, `Revocation.feature`)
is implemented, with one correction commit (an invalid separator after `Basic` is a malformed Basic
attempt, never a request without Basic); the details its implementation settled are in §9.3. P3.1
(`KeycloakTokenRevocationManager` with the client-type gate) is implemented, with two
correction commits (an error member, or a member name, holding an escaped, unpaired surrogate is an
unparseable provider answer, never a 500); A-01 and A-03 are closed by it and the details it settled
are in §9.3. P3.2 registers the manager in Keycloak mode and adds the Keycloak observer and the
public-client scenarios to `Revocation.feature`; the details it settled are in §9.3. P4.1 makes the
manager a required endpoint dependency, adds the D-12 startup check and amends the 8.1.0 changelog,
with two correction commits (the route's failures are logged by exception type names only, because
request-time DI construction can raise exceptions no revocation boundary sees; and their content is
withheld before the framework's exception middleware, which logs an exception it cannot answer); the
details it settled are in §9.3. P4.1 was approved through its third correction (`9fbb94f12`). P5.1
rewrites the revocation sections of `CS-AUTH.md`, the Keycloak posture of `OWASP-AUTH-COVERAGE.md`,
adds the revocation section of `KEYCLOAK-SETUP.md`, links this document from the design index, and
records the known limitations in §9.4; its final verification is recorded in §9.2. Revisions 1 and 2
were reviewed by Codex and not approved; §0.1 maps each finding to the change made.
**Ticket:** [DMS-1327](https://edfi.atlassian.net/browse/DMS-1327). Prerequisite: DMS-1478 / PR #1280
(commit `8b43a5fee`). Related: DMS-1218 (CMS error contract), DMS-1365 (Keycloak compensation).
**Branch / worktree:** `DMS-1327` at `C:\wt\DMS-1327`, clean, on `f3a44e12b` (main).
**Protocol references:** RFC 7009 (§2.1 client authentication, §2.2 response), RFC 6749 §2.3 (client
authentication), §5.2 (error response).

---

## 0. How to read this document

Section 1 is the baseline established by read-only inspection. Section 2 is the Keycloak evidence
and what is *not* yet known. Section 3 holds the design decisions (D-nn), each one traceable to an
AC. Section 4 is the response contract with precedence. Section 5 is the AC traceability table.
Section 6 is the phased, numbered implementation plan with per-step verification and approval
checkpoints. Section 7 lists assumptions and decisions that need a human answer. Section 8 is the
adversarial review checklist for Codex.

Conventions: `F-nn` = finding, `D-nn` = decision, `A-nn` = assumption, `Q-nn` = open question,
`P<phase>.<step>` = implementation step.

### 0.1 Revision log (revision 2, responses to the Codex review of revision 1)

| Codex finding | Change |
|---|---|
| 1. Keycloak could mutate before the public-client gate existed (old P3.1 → P3.2) | Phase 3 restructured: P3.1 builds the manager **with** the gate and leaves it unregistered (no executable path); P3.2 activates registration and runs E2E. No commit registers a Keycloak revocation manager that lacks the gate. |
| 2. Gate did not define fail-closed outcomes | D-11 rewritten: affirmative evidence of exactly one ordinal-matching client with `publicClient` explicitly `false`; every other outcome enumerated; admin-read timeout and cancellation specified (D-11.4). |
| 3. Logging sanitized provider `error` values | D-15 now logs provider codes only through a fixed allowlist, else the literal category `unrecognized`; `error_description` is never logged; D-07's "logged as today" is reconciled in D-07.5 with a test that the revocation log never contains the token or secret. |
| 4. 503 text asserted "not revoked" | D-01 text is now "Token revocation could not be confirmed."; D-13.3 documents retry/idempotency and the confirmation path; P2.1 and P3.1 include a mutation-then-response-failure test that asserts 503 and asserts nothing about final state. |
| 5. OAuth contract vs Ed-Fi 500 | D-17 (new): the revoke route carries endpoint metadata that switches `GlobalExceptionHandler` to the OAuth writer; unexpected exceptions → **500** `server_error` in OAuth JSON, logged through the existing pipeline; malformed forms → 400 `invalid_request` via the same writer; transport-level failures before the endpoint are out of contract and listed. HTTP-pipeline tests required. |
| 6. Precedence inconsistency, decoding | D-03 row 4 now keys on **field presence** (including empty values) and runs before Basic parsing; P2.3 cases corrected (malformed Basic + form fields → 400; malformed Basic alone → 401). D-04 decoding uses form decoding (`+` → space, percent escapes) per RFC 6749 §2.3.1; multiple `Authorization` values → 400 `invalid_request`; focused cases listed. `/connect/token` untouched. |
| 7. AC8 provider verification incomplete | P1.1 asserts exact values and token state through introspection, defines the token-type matrix per provider and isolated data/cleanup; P3.2 and P2.3 add self-contained cross-client active-before/unchanged-after, Basic **and** form success on both providers, and a public client supplying an arbitrary secret. |
| 8. `-SkipDockerBuild` after production edits | All E2E commands after a production change now rebuild (`-r`), and the evidence records the image id and git SHA (§6.8). |
| Key loading detail | D-07.4 specifies database vs certificate paths, per-key import failure, empty key set, and unknown `kid`; no exception-swallowing loader is added. |
| Startup detail | D-12 covers a throwing factory or missing dependency with a sanitized critical log and no provider contact. |
| Q-01…Q-07 | Recorded as resolved in §7 with the review's decisions. |

Revision 3 (responses to the Codex review of revision 2):

| Codex finding | Change |
|---|---|
| 1. Logging guarantee unsupported on exception paths | D-07.5: revocation logs a fixed category (`TokenVerificationFailure` name) instead of `Detail`; severities unchanged. D-12: startup logs fixed text plus the exception **type chain** only and rethrows without the foreign inner exception. D-15: rule that no exception reaching the pipeline from revocation code may carry caller or provider content, dependency exceptions are classified and logged type-only, and disclosure tests inspect state, scopes, the attached exception and every inner exception, with a secret sentinel planted in a dependency exception message and in an unverified `kid`. D-17: the 500 path is covered by the same rule and test. |
| 2. Public-client state cannot be introspected with the owner's credentials | D-16, P1.1, P3.2: a separate confidential **observer** client introspects every Keycloak token in evidence; observer credentials are never submitted to revocation; the observer proves `active:true` before and is reused after; active-before is required only for rows that start from a live token. Keycloak 26.1 source confirms introspection returns 403 "Client not allowed." for public clients and performs no ownership check (A-06). |
| 3. Decoding APIs not strict | D-04 rewritten: explicit base64 alphabet/padding validation before `Convert.FromBase64String`, a hand-written percent decoder that rejects `%`, `%2`, `%GG`, strict UTF-8 (`throwOnInvalidBytes`) applied **after** percent decoding, test cases added. |
| P1.1 expiration | bounded polling of the observer's introspection until `active:false`, with a deadline derived from the token's `exp`. |
| Version isolation | the 26.7 run uses its own compose project name and volume and is torn down afterwards. |
| Image evidence | container name corrected to `ed-fi-api-config-service`. |
| Commit/test ordering | §6.8 now defines: focused tests on the working tree, commit, then E2E against the clean commit, fix-forward commits if needed, and final E2E against the final commit. |
| Stale reference | P2.1 now says Keycloak activation happens at P3.2. |

P2.1 second correction (responses to the Codex review of commit `b8c1d4b4b`):

| Codex finding | Change |
|---|---|
| A truncated stored hash still became `invalid_client`: `BinaryReader.ReadBytes` returns a short array without throwing, so a hash missing its last byte compared a 31-byte subkey and answered `false` | D-07 step 2: the failure-preserving path checks that the stored hash holds the complete declared salt and the 32-byte subkey before comparing, and reports structural corruption as an `InvalidDataException` with fixed text, classified `TemporarilyUnavailable`. The lenient path is unchanged. Regressions use a truncated real generated hash at the hasher, manager and HTTP levels. |
| Q-09 | Resolved as a plugin compatibility requirement (§7); no contract change. |
| Validator limitation | D-07.6 no longer claims the "same key pair" rationale bounds the risk; the limitation is recorded as residual. |

P2.1 correction (responses to the Codex review of commit `3cef7b53a`, the P2.1 commit):

| Codex finding | Change |
|---|---|
| 1. The registered `ClientSecretHasher` swallowed verification failures (logging the exception and message) and returned `false`, so a hashing or configuration failure became `InvalidClient`/401 | D-07 step 2 and D-13.1: revocation verifies through `IFailurePreservingSecretVerifier`, implemented by the built-in hasher, which answers `false` only for a genuine mismatch and lets every other failure escape unlogged; the manager's boundary classifies it as `TemporarilyUnavailable` and logs exception type names only. `VerifySecretAsync`, which `/connect/token` uses, is unchanged. Real-hasher tests at the manager, hasher and HTTP levels. |
| 2. Revocation reused the JWKS certificate helper, which creates a development certificate when the file is missing, so revocation could verify against a fresh unrelated key and answer an empty 200 | D-07.4: revocation loads certificates read-only; a missing or unconfigured certificate is `TemporarilyUnavailable` (`signing-certificate-missing`) and no file is created. The JWKS and signing paths keep provisioning. Missing, existing and unreadable certificate tests plus a JWKS provisioning test. |
| Spec | D-13.1 gains the rule that catching around a dependency is insufficient when the dependency suppresses failures or repairs missing state internally; D-07.6 records the audit of every helper the revocation path reuses. Q-09 added for replacement (plugin) hashers. |

P1.1 correction (responses to the Codex review of commit `7cebf3660`, the first P1.1 commit):

| Codex finding | Change |
|---|---|
| 1. Failure diagnostics could disclose raw provider content | Every diagnostic the characterization harness raises is built from fixed text, the operation label, the HTTP status and a body **category** (empty / JSON object / JSON array / JSON value / non-JSON); request and response content never reach an exception. A `SensitiveValueRegistry` records every secret and token the run sees, and the evidence log refuses any row that would contain one. Offline tests with sentinel secrets (`KeycloakCharacterizationDiagnosticsTests.cs`) walk each exception's message, string form and inner chain. |
| 2. The compatibility run did not characterize refresh-token revocation | D-16 corrected (approved): refresh tokens are introspected by their **owning** client with `token_type_hint=refresh_token`; the separate observer stays for access tokens; a public client's refresh token is probed with a refresh grant as the last action on that token. K-14 is now characterized on 26.1.4 **and** 26.7.5 with identical results. |
| 3. "Empty success" did not assert an empty response | Every row predicts a body shape: `empty` asserts zero body length; `OAuth JSON` asserts a JSON object with `Content-Type: application/json`. |
| 4. Evidence writing bypassed cleanup | The run's teardown writes the evidence inside `try` and disposes the realm resources in `finally`; if both fail, both exceptions are reported. |
| Decisions | §2.1 wrong-secret row corrected to the observed `401 unauthorized_client` (D-10 row 4 unchanged); the test-only observer audience scope kept, creation and removal confined to fixture-owned resources; version wording distinguishes observed upstream `26.1.4` / `26.7.5` behavior from the Red Hat build of Keycloak 26.4 migration statement about `26.4.12`; Q-08 resolved: the fixture stays in the existing Keycloak CI lane with every prediction exact; the fixture/harness file split is accepted. |

---

## 1. Baseline (read-only inspection, confirmed against the branch)

### 1.1 Current request path (`IdentityModule.RevokeToken`)

File: `src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore/Modules/IdentityModule.cs:376-465`.

| ID | Finding | Evidence |
|---|---|---|
| F-01 | Basic and form credentials are combined: Basic is parsed first, and each form field is used as a *per-field* fallback, so `Basic(id:secret)` + form `client_id=other` silently proceeds with Basic, and `Basic(id:)` + form `client_secret=x` combines the two. | `IdentityModule.cs:385,400-407` |
| F-02 | Malformed Basic credentials (bad base64, no colon) are logged and then fall through to form credentials. | `TryParseBasicAuthCredentials` `IdentityModule.cs:153-200` |
| F-03 | Duplicate form parameters are concatenated by `StringValues.ToString()` (`"a,b"`), never rejected. | `IdentityModule.cs:394-406` |
| F-04 | Missing `token` returns the Ed-Fi `application/problem+json` contract (DMS-1218 INV-18), not an OAuth error. | `IdentityModule.cs:416-419` |
| F-05 | Non-form bodies are treated as an empty form (then "missing token"); a malformed form (`InvalidDataException` from `ReadFormAsync`) is shaped by `GlobalExceptionHandler` into Ed-Fi 400. | `IdentityModule.cs:389`, `GlobalExceptionHandler.cs:53-57` |
| F-06 | `invalid_client` is always 401, with the `WWW-Authenticate: Basic` challenge added only when the header starts with `Basic`. The ticket requires 400 when Basic was not attempted. | `IdentityModule.cs:480-496` |
| F-07 | In Keycloak mode `ITokenManager` is `KeycloakTokenManager`, which does not implement `ITokenRevocationManager`, so the handler returns a bare 200 **before** any credential check. Anonymous callers get 200. | `IdentityModule.cs:424-427`, `KeycloakTokenManager.cs:12-16` |
| F-08 | Every exception from `RevokeTokenAsync` is swallowed and answered 200. | `IdentityModule.cs:447-464` |
| F-09 | The provider-mode check (F-07) runs *before* the credential presence check, so the no-op branch is reachable unauthenticated. | `IdentityModule.cs:421-434` |
| F-10 | `token_type_hint` is read but never used (acceptable for self-contained; must be defined for Keycloak). | `IdentityModule.cs:395` |

### 1.2 Current manager path (`OpenIddictTokenManager`)

File: `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/OpenIddictTokenManager.cs`.

| ID | Finding | Evidence |
|---|---|---|
| F-11 | `ITokenRevocationManager` lives in the OpenIddict project (`Token/ITokenRevocationManager.cs`) and splits `AuthenticateClientAsync` from `RevokeTokenAsync`. Keycloak cannot implement that split: its authentication happens inside the delegated revoke request. | interface file; `KeycloakServiceExtensions.cs:46` |
| F-12 | `RevokeTokenAsync` catches every exception and returns `false`, so a database outage during token verification or the `UPDATE` is reported as the "unknown token" outcome (200). | `OpenIddictTokenManager.cs:624-628` |
| F-13 | `GetPublicKeysFromDatabaseAsync` catches every exception and returns an **empty** key set. A database failure during signing-key retrieval therefore makes every token fail verification as `Untrusted` → `false` → 200. This is a second, independent masking path that removing the catch in F-12 does **not** fix. | `OpenIddictTokenManager.cs:784-787` |
| F-14 | `AuthenticateClientAsync` returns only the canonical `client_id`; the application's `Id` (the `OpenIddictToken.ApplicationId` foreign key) is discarded, so the manager cannot constrain the update by application. | `OpenIddictTokenManager.cs:346-355` |
| F-15 | `AuthenticateClientAsync` lets repository/hasher exceptions propagate; the endpoint has no catch around it, so they surface as a `GlobalExceptionHandler` 500 (Ed-Fi contract), not 503. | `IdentityModule.cs:441` |
| F-16 | Ownership comparison is `StringComparison.Ordinal` on the verified `client_id` claim vs the canonical caller id (DMS-1478 protection; preserve). | `OpenIddictTokenManager.cs:650` |
| F-17 | Signature, issuer, audience and lifetime (5-minute skew) are verified before any claim is trusted (DMS-1478 protection; preserve). | `JwtTokenValidator.cs:89-165` |

### 1.3 Repositories

| ID | Finding | Evidence |
|---|---|---|
| F-18 | PostgreSQL `RevokeTokenAsync(Guid)` is `UPDATE ... WHERE "Id" = @Id`; MSSQL is the same with `SYSUTCDATETIME()`. Neither constrains by `ApplicationId`, and both overwrite `RedemptionDate` on re-revocation. | `Postgresql/.../OpenIddictDataRepository.cs:499-508`, `Mssql/.../OpenIddictDataRepository.cs:543-552` |
| F-19 | `GetApplicationByClientIdAsync` differs by engine: PostgreSQL single aggregate query, case-sensitive; MSSQL four queries via `GetApplicationInfoAsync`, case-insensitive under default collation. Both return `ApplicationInfo.Id`. **Unchanged by this ticket (AC1).** | `Postgresql:311-345`, `Mssql:324-419` |
| F-20 | `TokenInfo` already exposes `ApplicationId`, `Status`, `ExpirationDate`; `GetTokenByIdAsync` exists on both engines. | `Models/TokenInfo.cs`, both repositories |

### 1.4 DI and startup

| ID | Finding | Evidence |
|---|---|---|
| F-21 | `ConfigureDatastore` registers `ITokenManager → OpenIddictTokenManager` (transient) **unconditionally**; `AddKeycloakServices` registers `ITokenManager → KeycloakTokenManager` afterwards, so last-wins picks Keycloak in that mode. Both engine OpenIddict extensions register `ITokenRevocationManager → OpenIddictTokenManager` as a **singleton**, but nothing resolves it today (the endpoint pattern-matches `ITokenManager`). | `WebApplicationBuilderExtensions.cs:232`, `PostgresOpenIddictServiceExtensions.cs:37,93`, `MssqlOpenIddictServiceExtensions.cs:35` |
| F-22 | `KeycloakContext` is **scoped**; `KeycloakTokenManager` is transient and depends on it. Any startup resolution of a Keycloak service must happen inside a created scope (pattern: `InitializeClaimsData` in `Program.cs:272`). | `KeycloakServiceExtensions.cs:43-46` |
| F-23 | Startup-abort pattern that is testable under `WebApplicationFactory`: log, then `throw InvalidOperationException` before `RunAsync` (plugin audit, `Program.cs:139-209`; `DatabaseOptionsStartupTests` asserts on the exception). `Environment.Exit(-1)` is used only inside database/claims init and is explicitly called out as untestable. | `Program.cs:95-104` |
| F-24 | The named `HttpClient` `"KeycloakClient"` already exists with `Timeout = AppSettings.TokenRequestTimeoutSeconds`. | `WebApplicationBuilderExtensions.cs:112-118` |
| F-25 | `KeycloakClientFacade` wraps `Keycloak.Net.Core` 1.0.29 using the CMS **service** credentials (`IdentitySettings:ClientId/ClientSecret`) for admin calls. `GetClientsAsync(realm)` exists; the repository filters client lists in memory. | `KeycloakClientFacade.cs:15-19`, `KeycloakClientRepository.cs:538` |
| F-26 | Keycloak mode maps **no** `IEnhancedTokenValidator`, so CMS `/connect/introspect` always answers `{active:false}` there. It cannot be the Keycloak validation path. | `IdentityModule.cs:347-350`, `WebApplicationExtensions.cs:27-37` |

### 1.5 Tests and E2E

| ID | Finding | Evidence |
|---|---|---|
| F-27 | DMS-1478 tests to preserve (migrate, never delete): `OpenIddictTokenManagerTests` (`Given_AuthenticateClientAsync_*`, `Given_RevokeTokenAsync_*`, 20+ fixtures), `IdentityModuleTests.RevocationOwnershipTests` (12 fixtures), `OAuthEndpointErrorTests.Given_a_revocation_request_*` (2 fixtures asserting the Ed-Fi 400 and the Keycloak-mode 200). The last two encode behavior this ticket deliberately changes (F-04, F-07). | test files |
| F-28 | Repository integration tests on both engines exercise `RevokeTokenAsync(Guid)` only incidentally (expired-token sweep, concurrency). No test asserts ownership in the mutation. | `*.Tests.Integration/OpenIddictDataRepositoryTests.cs` |
| F-29 | CMS E2E: `OwaspCriticalPaths.feature` scenario 18 (`@SelfContainedOnly`) proves active-before / 401-after on a protected resource. The step `the current token is revoked` authenticates with Basic using the last token-request credentials. `SetupHooks` has a `SelfContainedOnly` tag hook; there is no Keycloak-only tag. | feature file lines 173-187, `StepDefinitions.cs:390-411`, `SetupHooks.cs:45-62` |
| F-30 | CI runs CMS E2E for both `keycloak` and `self-contained` on PostgreSQL, and MSSQL with `TestCategory=MssqlRepresentative` for both providers. | `.github/workflows/on-config-pullrequest.yml:382,438,476,532` |
| F-31 | Keycloak in the E2E stack is `quay.io/keycloak/keycloak:26.1@sha256:044a457e…` on `localhost:8045`, realm `edfi`, admin `admin/admin`, clients created by `setup-keycloak.ps1` with `publicClient=false`, `serviceAccountsEnabled=true`. | `eng/docker-compose/keycloak.yml`, `.env.config.e2e`, `setup-keycloak.ps1:426-436` |
| F-32 | `eng/azure-vm/compose/keycloak.yml` references `keycloak:26.7` (security-review VM, not CI). Not used by any test. | file |

### 1.6 Documentation touched by DMS-1478 (must be updated, not contradicted)

`reference/design/configuration-service/CS-AUTH.md` (states Keycloak mode is an unauthenticated
no-op), `docs/OWASP-AUTH-COVERAGE.md` (same, calls it a "deliberate gap"), `docs/changelog/8.1.0.md`
(release note says Keycloak unchanged), `reference/adr-client-id-casing.md` (unchanged by this
ticket), `eng/docker-compose/KEYCLOAK-SETUP.md`.

**DMS-1218 relationship.** DMS-1218 converted the revoke missing-token 400 to the Ed-Fi contract
(INV-18, commit C12). DMS-1478 then put `invalid_client` back in OAuth format with the explicit
rationale that `/connect/revoke` is an OAuth endpoint. The Jira description for this ticket
("Apply the table below using OAuth JSON errors for revocation") makes the OAuth format the rule
for **every** non-2xx response from `/connect/revoke`. The DMS-1218 design document is historical
and is not edited; CS-AUTH.md and the changelog will state the exception explicitly (D-01).

---

## 2. Keycloak evidence and the evidence gate (AC6)

### 2.1 What the pinned upstream source says (Keycloak tag `26.1.0`)

Read from `services/src/main/java/org/keycloak/protocol/oidc/endpoints/TokenRevocationEndpoint.java`
and `services/src/main/java/org/keycloak/authentication/authenticators/client/ClientIdAndSecretAuthenticator.java`
at tag `26.1.0`. **This is source reading, not observed behavior.** Nothing below may be coded
until P1.1 has recorded the actual responses from the running image (D-14).

Order of checks in `revoke()`: `checkSsl` → `checkRealm` → `checkClient` (client authentication) →
`checkParameterDuplicated` → client policies → `checkToken` → `checkIssuedFor` → `checkUser` →
revocation.

| Situation (per source) | Expected HTTP | Expected body (`error` / `error_description`) |
|---|---|---|
| Success | 200 | empty |
| `token` missing | 400 | `invalid_request` / "Token not provided" |
| Token does not decode (`session.tokens().decode` returns null) | **200** | `invalid_token` / "Invalid token" |
| Token type not `Bearer`/`Refresh`/`Offline`/`DPoP` (e.g. an ID token) | 400 | `unsupported_token_type` / "Unsupported token type" |
| `azp` (`issuedFor`) missing | 200 | `invalid_token` / "Invalid token" |
| `azp` ≠ authenticated client | **400** | `invalid_request` / **"Unmatching clients"** |
| Session/user not resolvable | 200 | `invalid_token` / "Invalid token" |
| Duplicated form parameter | 400 | `invalid_request` / "duplicated parameter" |
| Bearer-only client | 400 | `invalid_client` / "Bearer-only not allowed" |
| Wrong secret, known confidential client | 401 | `unauthorized_client` / "Invalid client or Invalid client credentials" — **corrected from the P1.1 evidence** (K-04, K-05, both versions); the source reading had predicted `invalid_client`. D-10 row 4 maps both codes |
| Unknown `client_id` (including a case variant of a real one), or no credentials at all | 401 | `invalid_client` / "Invalid client or Invalid client credentials" — observed in P1.1 (K-03, K-06, K-07, both versions) |
| **Public client** | **client authentication succeeds** with or without a secret (`if (client.isPublicClient()) { context.success(); return; }`) | — |
| Basic header **and** form `client_id` | form value **overrides** the header | — |
| Access-token revocation effect | `jti` written to the single-use "revoked" store for the token's remaining lifetime; no session removal | — |
| Refresh/offline-token revocation effect | client session detached from the user session (all tokens of that session) | — |

Two facts matter most for the design:

1. Keycloak itself will **not** reject a public client. Delegation alone cannot satisfy "public
   clients must be rejected without changing token state" (D-11).
2. Keycloak **tolerates** mixed and duplicated mechanisms in ways CMS must not. CMS must reject
   them before delegation (D-05).

### 2.2 What is unknown until characterization

- Whether `session.tokens().decode` rejects an **expired** but correctly signed token (→ 200
  `invalid_token`) or accepts it and reaches the ownership check (→ 400 "Unmatching clients" for a
  cross-client token). Either outcome maps to CMS 200 under D-13, so it affects evidence, not design.
- Whether an unknown `client_id` yields 401 `invalid_client` or 400 `invalid_request`.
- Whether an **unrecognised** `token_type_hint` value is ignored (the endpoint source does not read
  it) and whether `refresh_token` as a hint changes anything for a client-credentials access token.
- Whether the realm in the E2E stack has `sslRequired` set so that the in-network `http://dms-keycloak:8080`
  URL passes `checkSsl` (it must, or every delegated request is 403 "HTTPS required").
- Whether `Keycloak.Net.Core` 1.0.29's `Client` model exposes `PublicClient` (A-01; package is not in
  the local NuGet cache on this machine).
- Whether 26.7 behaves identically (Q-02).

### 2.3 Version decision (Q-02, resolved by review)

The pinned, digest-locked `26.1` image is the one CI runs and the one DMS-1218/DMS-1356 already
probed. It is the **required baseline** for this ticket's verification. The characterization suite
is also run once against `26.7` locally (temporary compose override of the image reference in the
scratch directory, nothing committed) and both result tables are recorded in §9.1 as a
compatibility record. This does not establish an official "26.1 only" support policy. Neither
compose file is changed by this ticket. If the two tables differ on a row CMS depends on (ownership
mismatch, unsupported type, invalid client, public client), the difference is resolved and
approved before P3.2 activates delegation.

---

## 3. Design decisions

### D-01 Error format for `/connect/revoke` (AC4)

Every non-2xx response from `POST /connect/revoke` is an RFC 6749 §5.2 JSON object
(`{"error": "...", "error_description": "..."}`, `Content-Type: application/json`), written by a
single private helper in `IdentityModule` (extending the existing `OAuthErrorResponse` record). The
`error_description` is always a fixed CMS string, never provider text. This is a deliberate,
documented exception to the DMS-1218 Ed-Fi contract and applies to this endpoint only. Success is
an empty 200 with no body (unchanged).

Codes used: `invalid_request` (400), `invalid_client` (400 or 401), `unsupported_token_type` (400),
`temporarily_unavailable` (503), `server_error` (500, D-17). `error_description` strings (fixed):

| Code | Description |
|---|---|
| `invalid_request` | one of: "The request body must be application/x-www-form-urlencoded.", "The request form payload is malformed.", "A request parameter or header was included more than once.", "Only one client authentication mechanism may be used.", "The token parameter is missing.", "The identity provider rejected the revocation request." (D-10 fallback) |
| `invalid_client` | "Client authentication is required." (no credentials) / "Invalid client or Invalid client credentials" (all other authentication failures, including public clients, so a public client is indistinguishable from a wrong secret) |
| `unsupported_token_type` | "The token type is not supported by the identity provider." |
| `temporarily_unavailable` | "Token revocation could not be confirmed. Retry the request and confirm the token's state through the provider's validation path." |
| `server_error` | "The revocation request could not be processed." |

The 503 text deliberately does not claim that the token was **not** revoked: a provider or
database can commit the change and then lose the response (D-13.3).

### D-02 Shared revocation contract in the common backend project (AC5)

New files in `src/config/backend/EdFi.DmsConfigurationService.Backend/` (namespace
`EdFi.DmsConfigurationService.Backend`, beside `ITokenManager.cs`):

```csharp
public interface ITokenRevocationManager
{
    Task<TokenRevocationResult> RevokeTokenAsync(
        TokenRevocationRequest request,
        CancellationToken cancellationToken);
}

/// Caller credentials plus the target token, after request-shape validation has passed.
public sealed record TokenRevocationRequest(
    string ClientId,
    string ClientSecret,
    string Token,
    TokenTypeHint TokenTypeHint);   // enum: None, AccessToken, RefreshToken  (unknown values -> None)

public abstract record TokenRevocationResult
{
    /// Empty 200: revoked, or unknown/invalid/expired/already-revoked/other client's token.
    public sealed record Completed : TokenRevocationResult;
    /// Caller failed client authentication (unknown, wrong secret, unapproved, public client).
    public sealed record InvalidClient : TokenRevocationResult;
    /// Provider recognised the token as a type it does not revoke.
    public sealed record UnsupportedTokenType : TokenRevocationResult;
    /// Provider rejected the request shape for a reason CMS could not pre-empt (Keycloak only).
    public sealed record InvalidRequest : TokenRevocationResult;
    /// Operational failure: database, signing-key retrieval, provider timeout/unreachable/5xx/malformed.
    public sealed record TemporarilyUnavailable(string Reason) : TokenRevocationResult; // Reason is sanitized, for logs only
}
```

The manager owns the ordering **authenticate → (type) → ownership → mutate** for its provider. The
endpoint owns request shape and the HTTP status/challenge rules. `Reason` never contains tokens,
secrets, headers or provider bodies. The OpenIddict `Token/ITokenRevocationManager.cs` is deleted.
Dependency direction is unchanged: OpenIddict → Backend, Keycloak → Backend, Frontend → all.

Why one method instead of the current two: Keycloak authenticates the caller inside the delegated
request (AC2), so a separate `AuthenticateClientAsync` cannot be implemented without a second
provider round-trip that would itself be an authentication side channel.

### D-03 Endpoint precedence (AC2, AC4)

Checks run in this order; the first failure answers.

| # | Check | Response |
|---|---|---|
| 1 | Request has no form content type | 400 `invalid_request` |
| 2 | `ReadFormAsync` throws `InvalidDataException` (malformed form) | 400 `invalid_request`, written by the OAuth branch of `GlobalExceptionHandler` selected by the route metadata (D-17) |
| 2a | A URL-encoded body `ReadFormAsync` accepts although its encoding is malformed: a `%` not followed by two hex digits, or a name or value that does not decode to valid UTF-8. `ReadFormAsync` keeps such escapes as literal text, so the endpoint checks the raw bytes as they are read (no buffering). The check is UTF-8 and `ReadFormAsync` decodes with the declared charset, so a `charset` other than UTF-8 (absent is accepted) is refused before parsing | 400 `invalid_request` "The request form payload is malformed.", or "The request form payload must be encoded as UTF-8." for the charset (added after review `dms-1327-01` finding 3) |
| 3 | The `Authorization` header carries more than one value, **or** any of `token`, `token_type_hint`, `client_id`, `client_secret` appears more than once (`StringValues.Count > 1`) | 400 `invalid_request` |
| 4 | "Basic attempted" (defined below) **and** the form **contains the key** `client_id` and/or `client_secret`, whatever their values (empty included, so partial and degenerate combinations are mixed too). Evaluated on key presence **before** the Basic value is parsed, so a malformed Basic value plus any form credential key is mixed, not malformed | 400 `invalid_request` |
| 5 | `token` missing or empty | 400 `invalid_request` |
| 6 | Basic attempted, no form credential keys, and the Basic value is malformed (undecodable base64, decoded text without `:`, empty id or empty secret after form-decoding) | 401 `invalid_client` + `WWW-Authenticate: Basic realm="EdFi.DmsConfigurationService"` |
| 7 | Basic not attempted and form `client_id` **or** `client_secret` missing/empty | 400 `invalid_client` |
| 8 | Manager → `InvalidClient` | 401 + challenge if Basic attempted, else 400 |
| 9 | Manager → `UnsupportedTokenType` | 400 `unsupported_token_type` |
| 10 | Manager → `InvalidRequest` | 400 `invalid_request` |
| 11 | Manager → `TemporarilyUnavailable` | 503 `temporarily_unavailable` |
| 12 | Manager → `Completed` | 200, empty |
| 13 | Any exception escaping the handler other than a caller cancellation | 500 `server_error` via D-17 (logged by the existing pipeline as `HttpRequestFailed`) |

Rules this encodes: request-shape validation (1–5) never touches credentials, token ownership, the
database or the provider. Authentication (6–8) precedes any token evaluation. An invalid or unknown
token cannot turn failed authentication into 200 because the manager returns `InvalidClient` before
it looks at the token. Operational failure (11) can only occur after 1–7 have passed, so an
infrastructure problem is never hidden behind a request-shape 400 or vice versa. Row 13 is reserved
for programming faults and is never used to express a provider or database outage.

"Basic attempted" is defined as: exactly one `Authorization` header value is present and its leading
scheme token (the longest run of RFC 7230 `tchar` characters at the start of the value) equals `Basic`
(case-insensitive), regardless of whether the rest of the value parses. Recognition looks at the scheme
token only. What follows it is validated afterwards by D-04 stage 0, and an invalid separator (a tab, a
tab followed by spaces, a comma, …) makes the attempt **malformed** (row 6, or row 4 when any form
credential key is present); it never turns the request into one without Basic, so it can never fall
back to form credentials. The `1*SP` rule below defines what a *valid* attempt looks like, not which
headers count as attempts. A value whose scheme token is longer (`Basicx …`) or different (`Bearer …`)
is not a Basic attempt. A
non-Basic `Authorization` header (for example `Bearer`) is **not** a client authentication attempt
for this endpoint and grants no authority; it is ignored and the form rules apply (Q-03, resolved).

Worked precedence examples (all become fixtures in P2.3):

| Request | Row | Response |
|---|---|---|
| `Basic` valid + form `client_id=` (empty) | 4 | 400 `invalid_request` |
| `Basic` malformed + form `client_secret=x` | 4 | 400 `invalid_request` |
| `Basic` malformed, no form credential keys | 6 | 401 `invalid_client` + challenge |
| two `Authorization` headers, both valid Basic | 3 | 400 `invalid_request` |
| `Bearer <token>` header + valid form credentials | 7/8 | authenticated by the form credentials only |
| `Bearer <token>` header, no form credentials | 7 | 400 `invalid_client` (no challenge) |
| no credentials + no token | 5 | 400 `invalid_request` |
| valid credentials + unknown token + database down | 11 | 503 |

### D-04 Basic parsing is strict for revocation and untouched for `/connect/token`

A new private parser returns `{NotAttempted, Malformed, Parsed(id, secret)}`. The existing lenient
`TryParseBasicAuthCredentials` keeps serving `GetClientAccessToken` unchanged (AC1 / non-goal:
no unrelated authentication refactoring; the `+` handling difference below is therefore confined
to `/connect/revoke` and is recorded in the release note).

Decoding follows RFC 6749 §2.3.1 exactly: the client applied `application/x-www-form-urlencoded`
encoding to `client_id` and `client_secret` **before** joining them with `:` and base64-encoding.
The framework APIs are more lenient than the rule, so each stage is validated explicitly rather
than trusted to throw:

| Stage | Rule | Why not the obvious API alone |
|---|---|---|
| 0. Separator | after the `Basic` scheme token: nothing (then stage 1 rejects the empty value) or one or more spaces (RFC 7235 §2.1 `1*SP`), which are skipped; any other character first, a tab included, → `Malformed` | the separator is part of the attempt, not part of recognising it (D-03) |
| 1. Base64 text | must be non-empty, length a multiple of 4, consist only of `A–Z a–z 0–9 + /` with `=` padding only in the last two positions; any other character (including whitespace) → `Malformed`. Only then `Convert.FromBase64String`. | `Convert.FromBase64String` silently skips whitespace |
| 2. Bytes → text | `new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString` → `DecoderFallbackException` → `Malformed` | default decoding substitutes U+FFFD |
| 3. Split | first `:`; none → `Malformed` | — |
| 4. Form-decode each part | hand-written decoder over the chars: `+` → byte 0x20; `%` must be followed by exactly two hex digits (`%`, `%2`, `%GG`, trailing `%` → `Malformed`) and yields that byte; any other char contributes its UTF-8 bytes. The resulting byte sequence is decoded with the same throwing UTF-8 decoder (`%FF`, `%C3` alone → `Malformed`). | `WebUtility.UrlDecode` preserves malformed escapes and decodes with a substituting decoder |
| 5. Emptiness | empty id or empty secret after step 4 → `Malformed` | — |

`Uri.UnescapeDataString` is not used because it does not decode `+`.

Focused cases (P2.3): id/secret containing a space encoded as `+` and as `%20`; a literal `+` in
the secret encoded as `%2B`; a `:` inside the secret (first-colon split keeps it); `%`, `%2`,
`%GG` and a trailing `%`; `%FF` and `%C3` (invalid UTF-8 after percent decoding); a non-UTF-8 byte
sequence under the base64; base64 with embedded whitespace, with a `-`/`_` (URL-safe alphabet),
with wrong padding length; a `Basic` scheme with no value; two `Authorization` values (row 3, not
row 6). Every malformed case asserts that the manager was not called.

### D-05 Mixed mechanisms and duplicates (AC2)

Defined in D-03 rows 3 and 4. Rationale: Keycloak lets a form `client_id` override the Basic header
(§2.1), so forwarding anything other than exactly one mechanism would let the caller choose which
identity Keycloak sees. CMS always forwards credentials to Keycloak as **form** fields
(`client_secret_post`) regardless of how the caller presented them, so Keycloak never sees two
mechanisms.

### D-06 `token_type_hint` (AC4)

Parsed into `TokenTypeHint`: `access_token` → `AccessToken`, `refresh_token` → `RefreshToken`,
anything else (including empty) → `None`. Self-contained ignores it entirely (the only token type is
the access token, located by verified `jti`). Keycloak forwards the hint only when it is
`AccessToken` or `RefreshToken`; `None` is not forwarded. A wrong hint therefore never blocks
lookup and never changes authorization on either provider.

### D-07 Self-contained manager (`OpenIddictTokenManager`) (AC1, AC3, AC5)

Sequence inside `RevokeTokenAsync(request, ct)`:

1. `GetApplicationByClientIdAsync(request.ClientId)` — engine lookup unchanged (F-19). Repository
   exception → `TemporarilyUnavailable`.
2. Secret verification and `IsApproved` (`ValidateClientSecretAsync`), through a
   **failure-preserving** path. The registered `ClientSecretHasher.VerifySecretAsync` answers
   `false` for a mismatch **and** for any failure while verifying (it catches, logs the exception
   with its message, and returns `false`), so catching around it cannot tell the two apart.
   Revocation therefore calls `IFailurePreservingSecretVerifier.VerifySecretPreservingFailuresAsync`
   (OpenIddict project; implemented by the built-in hasher): `true` = match, `false` = genuine
   mismatch (including an empty presented secret or an application with no stored secret), and any
   failure that prevented the comparison escapes as an exception that the hasher does not log:
   iteration-count misconfiguration, a stored hash that cannot be decoded, and a stored hash that
   decodes but is **structurally incomplete** (shorter than its declared salt plus the 32-byte
   subkey). The completeness check is explicit because `BinaryReader.ReadBytes` returns a short
   array instead of throwing, so a hash missing even one trailing byte would otherwise reach the
   fixed-time comparison and answer "wrong secret"; it throws `InvalidDataException` with fixed
   text. Mismatch or unapproved →
   `InvalidClient`; escaped failure → `TemporarilyUnavailable` (`client-authentication`), logged by
   the manager with exception type names only. `/connect/token` keeps `VerifySecretAsync` and its
   behaviour unchanged (AC1, non-goal), including its quiet `false` for an incomplete hash. A
   replacement hasher registered through the plugin contract is called through `VerifySecretAsync`;
   it must meet the plugin compatibility requirement in Q-09 for revocation to keep this guarantee.
3. Canonical client id = `CanonicalClientId(applicationInfo, request.ClientId)` (unchanged);
   `applicationId = applicationInfo.Id`.
4. Load verification keys through a **new private path** (`LoadVerificationKeysAsync`) used only
   by revocation. The public `GetPublicKeysAsync` (JWKS endpoint, bearer `IssuerSigningKeyResolver`)
   and `ValidateTokenAsync` keep their current behavior; `VerifyTokenAsync` takes the key dictionary
   as a parameter so both callers share the validator but not the loader. The new loader swallows
   nothing:

   | Situation | Classification | Why |
   |---|---|---|
   | `UseCertificates = true`: certificate path unset or file missing (development **or** production) | `TemporarilyUnavailable` (`signing-certificate-missing`), logged at Error; **no file is created** | the JWKS helper `GetPublicKeysFromCertificatesAsync` creates a replacement development certificate when the file is missing; reusing it would verify outstanding tokens against an unrelated key and answer an empty 200 while revoking nothing. Revocation uses its own read-only loader; the JWKS and signing paths keep provisioning |
   | `UseCertificates = true`: certificate exists but is unreadable or the password is wrong | `TemporarilyUnavailable` (`signing-key-retrieval`) | the loader throws to the boundary catch |
   | `UseCertificates = true`: certificate carries no RSA public key | `TemporarilyUnavailable` (`signing-certificate-no-rsa-key`), logged at Error | nothing can be verified |
   | database path: `GetActivePublicKeysAsync` throws (connection, timeout, SQL error) | `TemporarilyUnavailable` | F-13 |
   | database path: a key record whose bytes fail every import format (`DetectKeyFormat` → `Unknown`) or whose import throws | `TemporarilyUnavailable`, logged at Error with the sanitized key id | a corrupt active key would otherwise make every revocation an "unknown token" 200 |
   | database path: zero active key records | `TemporarilyUnavailable`, logged at Error | nothing can be verified; a configuration state, not a token outcome |
   | token `kid` absent or not among the loaded keys | token outcome: `Untrusted` → `Completed` | ordinary untrusted token against a healthy key set |

   The existing `_keyFormatCache` is reused read-only for format lookup; a cache miss detects and
   stores as today.
5. `JwtTokenValidator.ValidateToken` (signature, issuer, audience, lifetime, 5-minute skew) — unchanged.
   Any failure → `Completed`. **Logging on the revocation path uses fixed categories only:** a new
   private `LogRevocationVerificationFailure(TokenVerificationFailure failure)` writes a fixed
   message per category (`Expired` at Debug, `UntrustedIssuerOrAudience` at Warning with the
   existing "verify the configured Authority and Audience" hint, `Untrusted` at Warning) and the
   sanitized canonical caller id. It does **not** write `verification.Detail`, because `Detail`
   incorporates `Microsoft.IdentityModel` exception messages and the token's unverified `kid`,
   neither of which this document can prove free of attacker-chosen content. The bearer-validation
   path (`ValidateTokenAsync` → `LogVerificationFailure` with `Detail`) is unchanged (AC1, out of
   scope). Severity per category and the single-log-per-failure property are preserved.
6. Ordinal compare of the verified `client_id` claim with the canonical caller id — unchanged.
   Mismatch → `Completed`, token untouched.
7. `jti` missing or not a `Guid` → `Completed`.
8. `RevokeTokenAsync(jti, applicationId)` (D-08). Exception → `TemporarilyUnavailable`. Return
   `Completed` whether or not a row changed (0 rows = unknown, already revoked, or stored for a
   different application; all are 200 by contract and logged at Debug with sanitized ids only).

**D-07.6 Audit of reused helpers (P2.1 correction).** Every dependency or shared helper the
revocation path calls was checked for internal suppression, repair, or logging before the
manager's boundary catch:

| Helper | Finding | Disposition |
|---|---|---|
| `ClientSecretHasher.VerifySecretAsync` | catches every failure, logs the exception and its message, returns `false`; an incomplete stored hash does not even throw (short `ReadBytes`) and simply compares unequal | revocation uses the failure-preserving path with an explicit completeness check (step 2); token endpoint unchanged |
| `GetPublicKeysFromCertificatesAsync` | creates a development certificate when the file is missing | revocation uses a read-only loader (D-07.4) |
| `GetPublicKeysFromDatabaseAsync` | swallows repository failures and skips bad keys (returns a partial or empty set) | already not used by revocation (`LoadVerificationKeysAsync`, D-07.4) |
| `DetectKeyFormat` | its outer catch logged a warning **with the exception attached**; it returns `Unknown` | now logs exception type names only (applies to the JWKS path too, which loses only the stack trace of an `RSA.Create` failure); `Unknown` is `TemporarilyUnavailable` on the revocation path |
| `ResolveKeyFormat` | caches the detected format, including `Unknown`, for the process lifetime | unchanged: a key that cannot be read stays unavailable until restart, which fails closed |
| PostgreSQL and SQL Server `OpenIddictDataRepository` (`GetApplicationByClientIdAsync`, `GetActivePublicKeysInternalAsync`, `RevokeTokenAsync`) and both `OpenIddictTokenRepository` wrappers | no internal catch, no logging; exceptions propagate | no change; the boundary catches classify them and log type names only (driver messages can carry connection details) |
| `JwtTokenValidator.ValidateToken` | catches everything and returns a token-outcome category (`Untrusted`, `Expired`, `UntrustedIssuerOrAudience`) | unchanged and shared with the bearer path (AC1). **Residual limitation, not bounded by this design:** if a key that imports successfully cannot be used for verification at the time of the request (the stored key changed after the token was issued, or a cryptographic provider becomes unavailable or refuses the key), the validator reports the token `Untrusted` and revocation answers 200 without revoking. Ordinary signature failures remain token outcomes; this ticket does not rewrite the validator or reclassify signature failures as outages. Recorded for the operator documentation (P5.1) |

Removed: the blanket `catch (Exception) { return false; }` (F-12). Replaced by classified catches at
each infrastructure boundary (`DbException`-derived and provider-specific types plus a final
`Exception` catch around the infrastructure calls only, never around the comparison logic).
`ValidateTokenAsync` (bearer path) keeps its catch-all → `false` (401), which is correct for a
resource request and out of scope.

`UnsupportedTokenType` and `InvalidRequest` are never produced by this provider: the self-contained
store issues one token type, and a JWT of another shape simply fails verification → `Completed`.

**After merging DMS-1556 from `main`.** DMS-1556 moved bearer validation, introspection and the JWKS
endpoint to an in-memory signing-key snapshot. Its sources discard unusable key rows, and its
`DevelopmentCertificateStore` creates a missing development certificate; it also removed
`GetPublicKeysFrom*`, `ResolveKeyFormat` and `_keyFormatCache` from the manager. Revocation keeps
`LoadVerificationKeysAsync` and every D-07.4 classification above, because the snapshot would
reintroduce both masking paths. Formats are now detected and imported through
`PublicKeyMaterialParser`, on every call and without a cache, so a corrected key row is picked up
without a restart and an unreadable one still answers 503. The loader calls
`GetActivePublicKeysAsync(CancellationToken.None)`, which the parameterless overload already
delegated to. The parser's outer catch again logs the exception attached, as before D-07.6's
`DetectKeyFormat` change; it is reached only when `RSA.Create` fails, so it carries no caller input
or secret.
DMS-1556's `UseSigningKeySnapshot` replaces the bearer scheme's message-received handler, so the
revocation route's bearer exemption is composed in front of it.

### D-08 Atomic ownership-constrained mutation on both engines (AC3, AC8)

`IOpenIddictTokenRepository.RevokeTokenAsync(Guid tokenId)` and
`IOpenIddictDataRepository.RevokeTokenAsync(Guid tokenId)` become
`RevokeTokenAsync(Guid tokenId, Guid applicationId)`:

```sql
-- PostgreSQL
UPDATE "dmscs"."OpenIddictToken"
   SET "Status" = 'revoked', "RedemptionDate" = CURRENT_TIMESTAMP
 WHERE "Id" = @Id AND "ApplicationId" = @ApplicationId AND "Status" <> 'revoked';
-- MSSQL
UPDATE dmscs.OpenIddictToken
   SET Status = 'revoked', RedemptionDate = SYSUTCDATETIME()
 WHERE Id = @Id AND ApplicationId = @ApplicationId AND Status <> 'revoked';
```

Returns `rows > 0`. The `ApplicationId` predicate is the stored-association check the AC asks for,
enforced in the statement itself rather than by a read-then-write. The `Status <> 'revoked'`
predicate makes re-revocation a true no-op (preserves the original `RedemptionDate`); the manager
still answers 200. No schema change. The single-argument overload is removed (its only callers are
the manager and tests).

### D-09 Keycloak manager (`KeycloakTokenRevocationManager`, new, Keycloak project) (AC2, AC3, AC6)

Request: `POST {KeycloakContext.Url}/realms/{KeycloakContext.Realm}/protocol/openid-connect/revoke`
through `IHttpClientFactory.CreateClient("KeycloakRevocationClient")` (the `TokenRequestTimeoutSeconds`
timeout of `"KeycloakClient"`, with automatic redirects off so a 3xx is mapped by D-10 rather than
followed; `"KeycloakClient"` and `/connect/token` are unchanged; corrected after review `dms-1327-01`
finding 1), body
`application/x-www-form-urlencoded`: `client_id`, `client_secret` (the **caller's**, never the CMS
service credentials), `token`, and `token_type_hint` only per D-06. `using var response`;
`cancellationToken` forwarded. Response body read as a string only to parse `error` and
`error_description` with `System.Text.Json`; the body is never logged, never returned, and reading
is bounded (`MaxResponseContentBufferSize` or a `Content.ReadAsStream` cap of 64 KiB; malformed or
oversize → treated as unparseable).

Exception classification:

| Exception | Result |
|---|---|
| `OperationCanceledException` when `cancellationToken.IsCancellationRequested` (caller went away) | rethrow (no response is written; not an outage) |
| `TaskCanceledException` with `cancellationToken` **not** cancelled (HttpClient timeout; inner `TimeoutException` in .NET 10) | `TemporarilyUnavailable("timeout")` |
| `HttpRequestException` (connection refused, DNS, TLS) | `TemporarilyUnavailable("unreachable")` |
| `HttpRequestException`/`IOException`/timeout **while reading the response** after the request was sent | `TemporarilyUnavailable("response-lost")`; the provider may already have revoked (D-13.3) |
| body larger than the cap, or non-JSON on a non-200 | `TemporarilyUnavailable("unparseable")` |
| any other exception from the HTTP call or body handling | `TemporarilyUnavailable("unexpected")`, logged at Error with exception type only |

### D-10 Keycloak response mapping (AC4, AC6) — subject to P1.1 evidence

| Keycloak response | CMS result | Note |
|---|---|---|
| 200, any body (including `invalid_token` bodies) | `Completed` | Keycloak already answers unknown/invalid tokens with 200 per RFC 7009 |
| 400, `error == "invalid_request"` **and** `error_description == <exact observed ownership-mismatch string>` (expected "Unmatching clients") | `Completed` | **The only normalized `invalid_request`.** Both fields must match exactly; the string is a constant copied from the P1.1 evidence table, not typed from memory |
| 400, `error == "unsupported_token_type"` | `UnsupportedTokenType` | no ownership information: this fires before `checkIssuedFor` in Keycloak, and CMS never decodes the token |
| 400 or 401, `error ∈ {"invalid_client", "unauthorized_client"}` | `InvalidClient` | CMS chooses 400 vs 401 by D-03, never by Keycloak's status |
| 400, `error == "invalid_request"` with any other description | `InvalidRequest` | fixed CMS description (D-01); never forwarded |
| 3xx (never followed, D-09), 403, 404, 5xx, any other status, missing/unparseable body on a non-200 | `TemporarilyUnavailable` | logged at Error with the integer status code and the D-15 error category only (403 "HTTPS required" / 404 wrong realm are operator misconfigurations; they are still "cannot revoke right now" to the caller, and the log line is the corrective signal) |

### D-11 Public (secretless) clients under Keycloak (AC2, AC6)

Evidence (§2.1): Keycloak authenticates a public client **without** a secret and ignores any secret
supplied, so delegation alone would let a public client that holds a user-flow token revoke it, and
a public client can defeat the "no secret → `invalid_client`" rule simply by sending any non-empty
secret. Two layers, both before delegation, both fail-closed.

**D-11.1 Local layer.** D-03 rows 6–7 reject a request with no secret. This stops the honest
public-client shape without a provider call but is not sufficient on its own.

**D-11.2 Supplementary client-type gate.** Authority: the Keycloak Admin REST API, read-only,
through `IKeycloakClientFacade` (new method `GetClientsByClientIdAsync(realm, clientId, ct)`
returning the raw list Keycloak answers for the `clientId` query parameter, or the full list filtered
in memory if the package offers no filter; A-01). The admin read uses the CMS service credentials
because it is an administrative read, not the revocation; the revocation request itself carries only
the caller's credentials (D-09). Ordering: request shape (endpoint) → D-11.1 → D-11.2 → delegated
revoke. The gate is evaluated before any request that can change provider state.

**D-11.3 Decision table.** Delegation requires **affirmative evidence**; everything else is one of
two rejections.

| Lookup outcome | Result | Delegation |
|---|---|---|
| Succeeds; exactly one client whose `clientId` equals the caller's with `StringComparison.Ordinal`; its `publicClient` is present and `false`; its `bearerOnly` is absent or `false` | proceed | yes |
| Succeeds; zero ordinal matches (including matches that differ only by case, which Keycloak would also refuse) | `InvalidClient` (authoritative "no such client") | no |
| Succeeds; exactly one match; `publicClient == true` | `InvalidClient` | no |
| Succeeds; exactly one match; `bearerOnly == true` | `InvalidClient` (Keycloak would answer `invalid_client` "Bearer-only not allowed"; CMS answers it first) | no |
| Succeeds; exactly one match; `publicClient` is null/absent or the record is otherwise incomplete (no `clientId`) | `TemporarilyUnavailable` ("client type could not be established"), logged at Error with the sanitized client id | no |
| Succeeds; more than one ordinal match | `TemporarilyUnavailable` (ambiguous; Keycloak enforces uniqueness, so this is a provider/model fault), logged at Error | no |
| Admin authentication fails or the read is refused (401/403 from the admin API, `FlurlHttpException` with those statuses) | `TemporarilyUnavailable`, logged at Error with the corrective text "the Configuration Service client lacks permission to read clients in the realm" | no |
| Any other non-2xx, connection failure, malformed response, deserialization failure | `TemporarilyUnavailable`, logged at Error with exception type and status only | no |
| Timeout (D-11.4) | `TemporarilyUnavailable("timeout")` | no |
| Caller cancellation (D-11.4) | rethrow `OperationCanceledException` | no |

"Not found" is `invalid_client` **only** on the first-row kind of success (an HTTP 2xx list the
service account was authorized to read). It is never inferred from a failed or refused read (Q-07,
resolved).

**D-11.4 Timeout and cancellation for the admin read.** `Keycloak.Net` calls do not take a
`CancellationToken` and run on their own `HttpClient`, so the facade wraps the call with
`Task.WaitAsync(timeout, cancellationToken)` where `timeout = AppSettings.TokenRequestTimeoutSeconds`
(the same budget the revoke call gets). Expiry → `TimeoutException` → `TemporarilyUnavailable`;
caller cancellation → `OperationCanceledException` rethrown. The abandoned underlying call completes
or fails on its own and its result is discarded; this is recorded as a limitation (it cannot be
aborted). The revoke request (D-09) is cancelled properly because it uses the named `HttpClient`
with the token. If A-01 turns out to allow a `CancellationToken`, the wrapper is replaced by direct
propagation at P3.1 without changing the decision table.

Cost: one admin read per revocation. Accepted for a low-volume endpoint; recorded as a limitation
(Q-01, resolved: keep). The read precedes Keycloak's authentication of the caller, so any request
supplying a client id and secret pays it, valid or not; expected low traffic does not bound that
(L-04, L-14).

### D-12 DI and startup registration check (AC7)

- `AddKeycloakServices` registers `ITokenRevocationManager → KeycloakTokenRevocationManager`
  (transient; it depends on the scoped `KeycloakContext`).
- Both engine OpenIddict extensions keep `ITokenRevocationManager → OpenIddictTokenManager`
  (singleton, now against the moved interface).
- `IdentityModule.RevokeToken` takes `[FromServices] ITokenRevocationManager` (required). The
  `ITokenManager` pattern match is removed.
- `Program.cs`: a new `EnsureTokenRevocationSupport(app)` runs immediately after
  `AuditPluginRegistrations(app)` and before `ReportInvalidConfiguration`. It creates a scope and
  calls `GetService<ITokenRevocationManager>()` inside `try`:

  | Outcome | Action |
  |---|---|
  | returns an instance | continue startup; the instance is discarded with the scope |
  | returns null (nothing registered) | `LogCritical` with the fixed message `"No token revocation manager is registered for AppSettings:IdentityProvider '{Provider}'. Register one for this provider or correct the setting, then restart."` then `throw new InvalidOperationException(sameMessage)` |
  | throws (factory threw, missing dependency, scope violation) | `LogCritical` with the fixed message `"The token revocation manager for AppSettings:IdentityProvider '{Provider}' could not be constructed ({ExceptionTypes}). Correct the registration or its dependencies, then restart."` where `{ExceptionTypes}` is the chain of exception **type names** (outer to innermost, `->`-joined, from `GetType().FullName`), then `throw new InvalidOperationException(sameMessage)` **without** the caught exception as inner |

  `{Provider}` is the configured value after `AppSettingsValidator` has restricted it to
  `keycloak`/`self-contained`, so it is safe to log. The caught exception is **not** passed to the
  logger and **not** wrapped: a registration factory can come from a plugin, and its message may
  carry anything (a connection string, a secret). Type names are the diagnostic; the stack trace is
  deliberately given up on this path, which the plugin audit's existing output format already
  accepts for shape problems. Unhandled before `RunAsync` → nonzero process exit (same mechanism as
  the plugin audit, F-23), assertable under `WebApplicationFactory`. Construction only:
  neither implementation performs I/O in its constructor (`OpenIddictTokenManager` stores options
  and repositories; `KeycloakTokenRevocationManager` stores the context, factory, facade and
  logger), so the check cannot introduce a provider-availability requirement; the Keycloak startup
  test runs with no Keycloak reachable and asserts no request was issued.
- Runtime outages never touch this path; they are `TemporarilyUnavailable` → 503 per request.

### D-13 Operational failures vs token outcomes (AC5)

**D-13.1 Classification.** Catching around a dependency is necessary but **not sufficient**: a
dependency that suppresses its own failures (returns a benign value after catching) or repairs
missing state internally (creates a file, falls back to defaults) hides the failure before the
boundary catch can see it. Every dependency on the revocation path therefore either propagates its
failures or is called through a failure-preserving entry point (D-07 step 2, D-07.4, D-07.6).
Any exception thrown by: application lookup, secret verification,
signing-key retrieval (D-07.4), token status/row lookup, the `UPDATE`, the Keycloak admin read
(D-11.3), or the Keycloak revoke call (D-09) → `TemporarilyUnavailable` → 503. These are caught
**only** at those boundaries; comparison and parsing logic is not wrapped, so a bug there is a
programming fault and surfaces as **500 `server_error` in OAuth JSON** through D-17, never as a
503 (which would relabel a bug as an outage) and never as a 200 (Q-04, resolved).

**D-13.2 What is not operational.** A token that fails verification, an unknown `jti`, a mismatched
owner, an already-revoked row, and a Keycloak 200 with an `invalid_token` body are token outcomes
(`Completed`). A Keycloak 400/401 `invalid_client` is an authentication outcome.

**D-13.3 Uncertain final state.** A 503 means CMS could not confirm the outcome. In particular:
the self-contained `UPDATE` may have committed before the connection dropped, and Keycloak may
have written the revoked-token entry before the response was lost (timeout while reading the
response). CMS does not and cannot assert either way, which is why the 503 text (D-01) says "could
not be confirmed". Retrying is safe: self-contained re-revocation is a no-op by D-08
(`Status <> 'revoked'`), and Keycloak answers 200 for an already-revoked token. Callers that must
be certain confirm through the validation path (D-16). Both managers carry a test in which the
mutation call succeeds at the dependency and the response is then lost; the test asserts 503 and
makes no assertion about the token's state.

### D-14 Evidence gate

No line of D-10 or D-11 mapping code is written until P1.1 has run against the pinned image and
its result table is appended to this document and approved. If a row contradicts D-10/D-11, the
affected decision is revised and re-approved first.

### D-15 Logging and secrets (AC8)

Log only: the sanitized client id (`LoggingUtility.SanitizeForLog`), the outcome category (the
`TokenRevocationResult` type name), the provider HTTP status code (an integer), the provider error
**category**, and exception type names. The provider error category is derived from the parsed
`error` member through a fixed allowlist and is never the raw value:

| Parsed `error` | Logged category |
|---|---|
| `invalid_request`, `invalid_client`, `unauthorized_client`, `unsupported_token_type`, `invalid_token`, `server_error`, `temporarily_unavailable` | that literal |
| anything else, missing, non-string, unparseable body | `unrecognized` |

Sanitization is not relied on for secrecy: a provider body such as `{"error":"<secret>"}` is logged
as `unrecognized`. Never logged or returned: the token, any secret, the `Authorization` header,
provider bodies, the provider's `error_description`, verification `Detail` (D-07.5), or any
exception object whose content the revocation code does not control.

**Exception rules on the revocation path (endpoint, both managers, facade method, startup check):**

1. Exceptions thrown **by dependencies** (repositories, hasher, key loader, `HttpClient`, JSON
   parsing of a provider body, `Keycloak.Net`/Flurl, DI activation) are caught at the boundary
   where they arise, classified (D-13.1), and logged with a fixed message plus the exception
   **type-name chain** only. The exception object is never passed to the logger on this path, so
   `Message`, `InnerException`, `Data` and `ToString()` never reach a sink.
2. Exceptions **constructed by** revocation code carry fixed text only; no interpolation of the
   token, credentials, headers, form values, provider content, or dependency messages. This is a
   code-review rule for every step and is pinned by the tests below.
3. Rules 1 and 2 do not bound every exception that reaches `GlobalExceptionHandler` /
   `RequestLoggingMiddleware` from this route (D-17). Request-time DI construction of the manager,
   or of anything it depends on, runs registration factories and constructors a plugin can supply,
   and their exceptions escape before any revocation boundary exists (corrected after review of
   `dd021d94a`, which still attached them). The route is therefore marked with
   `ExceptionTypeOnlyLoggingMetadata`, and every exception raised on it, programming faults
   included, has its content withheld at `ExceptionContentBoundaryMiddleware`, immediately after
   routing and **inside** `UseExceptionHandler` (D-17 logging boundary; corrected after review of
   `f4da39efe`, which sanitized only in `RequestLoggingMiddleware`, outside the framework's
   exception middleware, so a response that had started or an error handler that failed still
   logged the original). No message, inner exception or `Data` of an exception raised on this route
   reaches any logger, the framework's included. Rule 2 still governs the text of exceptions
   revocation code constructs.
4. The strict Basic parser (D-04) logs nothing but a fixed phrase and the stage name; the existing
   lenient parser's type-name/sanitized-message log stays with `/connect/token`.

**Disclosure tests** (every fixture uses a capturing `ILogger` that records, per call: level,
rendered state, every key/value in the state, every active scope, and the attached exception with
its full inner chain; the assertion walks all of them, not only the rendered message):

| Fixture | Sentinel placement | Asserts absent from every captured field |
|---|---|---|
| Self-contained: expired, untrusted, wrong-audience, unknown-`kid` tokens | token string; a `kid` header value containing `SECRET-KID-SENTINEL`; secret string | all three |
| Self-contained: repository/hasher/key loader throws with `SECRET-EX-SENTINEL` in `Message` and in an inner exception | the exception | sentinel, token, secret |
| Keycloak: provider body with sentinel in `error`, in `error_description`, in an unexpected member; non-object body; 200 with body | body | sentinel, token, secret, Basic header value |
| Keycloak: handler/facade throws with the sentinel in `Message` and inner; `JsonException` on a body containing the sentinel | the exception | sentinel, token, secret |
| Endpoint: malformed Basic value containing the sentinel; duplicated parameters containing it | header/form | sentinel |
| Endpoint (D-17): fake manager throws an exception with fixed text | — | token, secret, header value, the exception's text; the failed-request event carries the type name and no exception object |
| Runtime (D-17): a registration that constructs at startup and throws on the next resolution, with the sentinel in `Message`, inner and `Data` | the exception | sentinel, from every log field, scope, attached exception and the response body; one failed-request event with the type chain and the correlation fields |
| Runtime (D-17): the same registration throws while binding after starting the response, or before it with the error-response write failing; every logger category captured at Trace | the exception | sentinel and the original exception types, from every record of every category (the framework's exception middleware does log both paths with an exception attached); the unmarked `/connect/token` control still logs the original |
| Startup (D-12): registration factory throws with the sentinel in `Message` and inner | the exception | sentinel; the critical log names the type chain |

### D-16 Validation paths (AC8 "validation and compatibility")

| Provider | Active-before proof | Revoked-after proof | Unchanged (cross-client / public client) proof | Propagation |
|---|---|---|---|---|
| self-contained | protected CMS resource 200 (existing scenario 18) and `/connect/introspect` `active:true` | protected CMS resource 401 (per-request status check by `jti`) and `/connect/introspect` `active:false` | after the attempt: protected resource still 200 **and** `/connect/introspect` still `active:true` | immediate |
| keycloak | Keycloak `POST {Url}/realms/{Realm}/protocol/openid-connect/token/introspect`: access tokens introspected by a dedicated confidential **observer** client that is in the token's `aud` → `active:true`; refresh tokens introspected by their **owning** client with `token_type_hint=refresh_token` → `active:true` | same endpoint, same introspecting client per token type → `active:false` | same endpoint, same introspecting client, still `active:true` | Keycloak-immediate; DMS/CMS validate JWTs locally and keep accepting the token until `exp` (+ their clock skew). No per-request introspection is added (non-goal). Scenario 18's 401 assertion is therefore **not** portable; `@SelfContainedOnly` stays on it and the Keycloak scenarios assert through introspection instead |

**Observer client (Keycloak), as corrected by P1.1.** Keycloak's introspection endpoint refuses
public clients (403 `invalid_request` "Client not allowed.", observed on 26.1.4 and 26.7.5), so a
public client's token can only be observed by someone else. **Access tokens** are observed by one
dedicated confidential client created for the test (`revocation-observer-<run id>`) whose
credentials are **never** submitted to `/connect/revoke`, and which is placed in the audience
(`aud`) of every observed token through a test-owned client scope carrying an audience mapper:
Keycloak 26.7.5 answers `active:false` to any introspecting client absent from `aud`, while 26.1.4
performs no such check (A-06). This separates the observer from the subject: an owner, a
cross-client caller and a public client are all observed the same way. **Refresh tokens** are
observed by their **owning** client with `token_type_hint=refresh_token`, because Keycloak's
refresh-token introspection admits only the client the token was issued to (26.7.5
`RefreshTokenIntrospectionProvider`; 26.1.4 answers the owner as well), so for refresh state the
owner's introspection is the first-hand observation and the observer-separation rule applies to
access tokens. A **public client's refresh token**, which its owner cannot introspect, is observed
through a refresh grant used as a liveness probe (200 = usable, 400 `invalid_grant` = not), performed
only as the last action on that token because the grant may rotate it. Each fixture first proves the
relevant observation path works (`active:true`, or a 200 refresh grant, for a freshly issued token)
before any revocation is attempted. P3.2's `[BeforeFeature]` observer setup creates the audience
scope and attaches it to the clients whose tokens the Keycloak scenarios observe, and removes it in
`[AfterFeature]`.

Supported token-type matrix (what "supported token" means per provider; the Keycloak rows are
confirmed or corrected by P1.1 before any documentation claims them):

| Token | self-contained | keycloak |
|---|---|---|
| Access token (JWT, `typ` Bearer) | supported; only type issued | supported |
| Refresh token | not issued | supported by Keycloak's endpoint; issued for `client_credentials` only when the client attribute `client_credentials.use_refresh_token` is `true`; **characterized on 26.1.4 and 26.7.5 (K-14)**: revocation answers 200 empty, and the refresh token and its paired access token both become inactive |
| Offline token | not issued | revocable at Keycloak; **not characterized** (needs `offline_access` and a user flow) and documented as outside this ticket's verification |
| ID token | not issued | `unsupported_token_type` |
| Related effects | none (no sessions, no refresh tokens) | access token: `jti` entered in Keycloak's revoked-token store for its remaining lifetime, session untouched (observed: the paired refresh token still buys a new access token, K-18); refresh: the client session is detached, which invalidates the paired access token (observed, K-14); offline: documented as source-derived only |

### D-17 OAuth error format for exceptions that escape the handler (AC4, AC8)

The revoke route is mapped with a marker metadata type (`OAuthErrorContractMetadata`, frontend
`Infrastructure`) via `.WithMetadata(...)`. `GlobalExceptionHandler.TryHandleAsync` reads
`IExceptionHandlerFeature.Endpoint` (it already does so for route binding) and, when the marker is
present, writes the OAuth JSON body instead of the Ed-Fi problem-details body:

| Exception | Status | OAuth body |
|---|---|---|
| `InvalidDataException` from `ReadFormAsync` (malformed form, D-03 row 2) | 400 | `invalid_request` / "The request form payload is malformed." |
| `BadHttpRequestException` 400 raised while the endpoint reads the body (e.g. form value count limits) | 400 | `invalid_request` / "The request form payload is malformed." |
| `BadHttpRequestException` 413 reaching the endpoint | 413 | `invalid_request` / "The request body is too large." |
| `BadHttpRequestException` 415 reaching the endpoint | 415 | `invalid_request` / "The request body must be application/x-www-form-urlencoded." |
| `BadHttpRequestException` with any other status (for example 408) | that status | `invalid_request` / "The request could not be read." (settled in P2.3: a client-side read failure keeps its client status, as the Ed-Fi branch does) |
| anything else | 500 | `server_error` / "The revocation request could not be processed." |

**Logging boundary** (corrected after review of `dd021d94a` and of `f4da39efe`). The route also
carries a second marker, `ExceptionTypeOnlyLoggingMetadata` (frontend `Infrastructure`). The framework's
exception middleware (`UseExceptionHandler`) logs, with the exception attached, any exception it cannot
answer: one raised after the response has started, and the original when the error handler itself
fails (for example while writing the error body). A handled response is not logged there. Sanitizing
outside that middleware is therefore too late, so the boundary is a middleware **inside** it:

- **`ExceptionContentBoundaryMiddleware`** (frontend `Middleware`) runs immediately after
  `UseRouting`, the first point the route is known. It wraps everything the route runs, endpoint
  parameter binding included, which an endpoint filter would not: a `[FromServices]` resolution can
  run plugin factories and constructors. For a marked route it catches every exception, records the
  original's type chain on a CMS-owned request feature (`WithheldExceptionFeature`), and throws a
  replacement of the same category with fixed text naming only that type chain, no inner exception
  and no `Data`:

  | Original | Replacement | Why the category matters |
  |---|---|---|
  | `OperationCanceledException` **or** `IOException` (`BadHttpRequestException` included) while `RequestAborted` is cancelled | `OperationCanceledException` for `RequestAborted` | the exception middleware treats either type as an aborted request: it logs the abort, answers 499 when nothing was sent, and invokes no error handler (corrected after review of `19612c773`, which turned an aborted-request `IOException` into a 500) |
  | `InvalidDataException` | `InvalidDataException` | D-17 table: 400 `invalid_request`, malformed form |
  | `BadHttpRequestException` | `BadHttpRequestException` with the same status code | D-17 table: 400/413/415/other client status |
  | anything else, an `IOException` on a request that was not aborted included | `InvalidOperationException` | 500 `server_error` |

  `GlobalExceptionHandler` maps the replacement exactly as it would have mapped the original, and the
  exception middleware, on the paths where it logs, logs only the replacement.
- **`RequestLoggingMiddleware`** (outermost) keeps the type-only failure event introduced after review
  of `dd021d94a`: for a marked route, read from `IExceptionHandlerFeature.Endpoint` or the active
  endpoint, it records one `HttpRequestFailed` event at Error with the same `EventName`, `Method`,
  `Path`, `StatusCode`, `DurationMs` and `TraceId` fields and the same request scope, plus
  `ExceptionTypes`, the original's chain from `WithheldExceptionFeature` (otherwise the chain of the
  exception it sees, or `none`), and **without** an exception object. An exception that escapes the
  pipeline is rethrown to the server as a further replacement (a caller cancellation stays an
  `OperationCanceledException`), so the server's own logger also sees fixed text only.

An exception raised on the route before routing (tenant resolution, the reverse-proxy and
security-header middleware) is outside the boundary: it is raised by CMS or framework code for every
route alike and cannot come from a revocation registration. The stack trace is given up on this route,
as for the D-12 startup check. The first two P4.1 commits relied on D-15 rules 1–2, which make
attachment safe only for exceptions raised inside the manager's boundaries; request-time DI
construction is outside them. `FailureResponseWriter` gains a
sibling `OAuthErrorResponseWriter` (frontend `Infrastructure`) used by both the handler and
`IdentityModule`, so the body shape has one definition. Other endpoints are unaffected because they
carry neither marker: their failures keep the exception attached and are rethrown unchanged.

Out of contract (documented, not changed): responses produced before the endpoint is selected
(Kestrel request-line/header limits, `FrameworkErrorResponseMiddleware`-shaped bodiless
framework responses before routing, TLS failures). Those are transport failures the endpoint never
sees.

Verification is through the HTTP pipeline (`WebApplicationFactory`): a manager fake that throws
`InvalidOperationException` → 500 OAuth JSON, `TraceId` header present, the request logged once as
failed with the type name and no exception object; a manager registration that throws at request
time with sentinels in its message, inner exception and `Data` → 500 OAuth JSON and no sentinel in
any log field, scope, attached exception or the body; the same registration failing after the
response started, and before it with the error-response write failing, with every logger category
captured at Trace: the framework's exception middleware logs both with an exception attached, and no
record carries the sentinel or an original exception, while the unmarked `/connect/token` control
still logs the original; `ExceptionContentBoundaryMiddleware` unit fixtures for each replacement
category and for unmarked routes; `RequestLoggingMiddleware` unit fixtures for the handled, escaping
and cancelled cases on a marked route; a malformed multipart body → 400 OAuth JSON; the same two requests against `/connect/token`
still produce the Ed-Fi contract (no leakage of the marker).

---

## 4. Response contract (the Jira table, as implemented)

| Condition | CMS response | Where decided |
|---|---|---|
| Authenticated owner, supported valid token | 200 empty; token revoked | D-07 / D-09 → `Completed` |
| Authenticated caller, invalid/unknown/expired/already-revoked supported token | 200 empty | D-07 steps 5–8, D-10 row 1, D-08 `Status <> 'revoked'` |
| Authenticated caller, another client's supported token | 200 empty; token unchanged | D-07 step 6 + D-08 predicate; D-10 row 2 |
| Missing/empty token, mixed mechanisms, duplicate required parameters, malformed form | 400 `invalid_request` | D-03 rows 1–5 |
| Missing/invalid credentials, Basic attempted | 401 `invalid_client` + `WWW-Authenticate: Basic` | D-03 rows 6, 8 |
| Missing/invalid credentials otherwise (incl. public client) | 400 `invalid_client` | D-03 rows 7, 8; D-11 |
| Provider-recognized unsupported token type | 400 `unsupported_token_type`, no ownership disclosure | D-10 row 3 (fires before Keycloak's ownership check; CMS never decodes) |
| Database failure, provider timeout, temporary unavailability | 503 `temporarily_unavailable`; no success claim and no "not revoked" claim | D-09, D-13 |
| (not in the Jira table) programming fault inside the handler or manager | 500 `server_error` in OAuth JSON; logged as a failed request | D-13.1, D-17 |

---

## 5. AC traceability

| AC / row | Decisions | Steps | Areas | Verification |
|---|---|---|---|---|
| AC1 preserve DMS-1478 | D-04 (token endpoint untouched), D-07 (steps 1–3, 5–6 unchanged), D-08 keeps lookup untouched (F-19) | P2.1, P2.2 | `OpenIddictTokenManager`, repositories, `adr-client-id-casing.md` unchanged | All DMS-1478 fixtures migrated and green: forged token, cross-client, case-only difference, non-canonical casing, expired, wrong issuer/audience, MSSQL/PG lookup tests untouched; `/connect/token` fixtures unchanged |
| AC2 authentication | D-03, D-04, D-05, D-11 | P2.1 (ordering in manager), P2.3 (endpoint shape), P3.1 (gate built with the manager), P3.2 (gate active before any delegation is reachable) | `IdentityModule`, both managers, `IKeycloakClientFacade` | `IdentityModuleTests`: every D-03 worked example, D-04 decoding cases, duplicates (4 params + header); manager tests: auth failure before any key/token repository call (`MustNotHaveHappened`); Keycloak tests: every D-11.3 row asserts **no revoke request was sent** unless the first row matched; E2E both providers: Basic **and** form success; public client with an arbitrary secret → 400 `invalid_client`, token still active |
| AC3 ownership | D-07, D-08, D-09 (caller creds only), D-11 | P2.2, P2.3 (E2E), P3.1, P3.2 | repositories (PG, MSSQL), Keycloak manager | PG+MSSQL integration: wrong `applicationId` leaves status `valid`; right app → `revoked`; second call → 0 rows, `RedemptionDate` unchanged; Keycloak manager test asserts the form body carries caller creds and never `KeycloakContext.ClientSecret`; E2E self-contained cross-client (resource 200 + introspect `active:true` after) and Keycloak cross-client (introspect `active:true` after) |
| AC4 response contract | D-01, D-03, D-06, D-10, D-17 | P2.1 (result mapping, 503), P2.3 (shape, status rules, 500 OAuth) | `IdentityModule`, `GlobalExceptionHandler`, `OAuthErrorResponseWriter` | `IdentityModuleTests` one fixture per §4 row and per precedence pair (shape vs auth, auth vs unknown token, unknown token vs outage, bug vs outage); hint ignored tests on both managers; pipeline tests for D-17 |
| AC5 shared abstraction, failure separation | D-02, D-07.4, D-13 | P2.1, P2.2 | Backend common, OpenIddict, Keycloak | Build (no inverted references: Backend gains no `ProjectReference`); manager tests: each boundary throws → `TemporarilyUnavailable`; each D-07.4 row; mutation-then-lost-response → 503 with no state assertion; comparison bug → 500 not 503 |
| AC6 Keycloak mapping | D-09, D-10, D-11, D-14 | P1.1 (evidence), P3.1, P3.2 | E2E characterization fixture, Keycloak manager | §9.1 exact observed values with before/after introspection; manager unit tests keyed on the observed strings; E2E: owner revoke (introspect false), cross-client (introspect true), invalid credentials, public client with arbitrary secret (introspect true) |
| AC7 startup/runtime | D-12, D-13 | P4.1 | `Program.cs`, DI extensions, changelog | Startup tests: registration removed → fixed message, no provider contact; registration factory throws → fixed message naming only the exception type chain (D-12), the original exception neither logged nor wrapped, no provider contact; all three provider/engine shapes boot; runtime: fake 503 leaves the host serving the next request; release note |
| AC8 verification & docs | D-15, D-16, §6.8 | every step; P5.1 | tests, `CS-AUTH.md`, `OWASP-AUTH-COVERAGE.md`, `changelog/8.1.0.md`, `KEYCLOAK-SETUP.md` | §9.2 evidence matrix with image ids and SHAs; logs asserted free of token/secret/header/provider body on every path; both DBs; both IdPs; token-type matrix verified or marked untested |

---

## 6. Implementation plan

Each step is one local commit (`[DMS-1327] …`), reviewed independently. Every step ends with:
`dotnet csharpier format <touched files>`, `./build-config.ps1 Build -Configuration Release`,
and the focused tests listed. Nothing is pushed before the final approval (§6.7). A step's
"checkpoint" means: stop, report (step id, SHA, files, behavior, ACs, checks, risks), wait.

### Phase 0 — Baseline and spec approval

**P0.1 Spec approval (this document).** Precondition for everything. On approval, the first
implementation step is P1.1 only. Commit boundary: this document is committed in P1.1 together
with the characterization fixture so the evidence lands beside the design.

### Phase 1 — Keycloak characterization (evidence gate, AC6)

**P1.1 Characterization fixture and evidence table.**
- Objective: record the pinned Keycloak image's actual responses before any mapping is coded. ACs: AC6.
- Preconditions: P0.1 approved; `pwsh ./setup-local-cms.ps1` (self-contained) is *not* enough —
  the stack must be started with the Keycloak provider:
  `cd eng/docker-compose; ./start-local-config.ps1 -EnvironmentFile ./.env.config.e2e -r -IdentityProvider keycloak -AddE2EClaimSets`
  (this is exactly what `build-config.ps1 E2ETest -IdentityProvider keycloak` runs).
- Files: new `src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Keycloak/KeycloakRevocationCharacterizationTests.cs`
  (plain NUnit, `[Category("KeycloakCharacterization")]`, `Assert.Ignore` unless
  `DMS_CONFIG_IDENTITY_PROVIDER=keycloak`; talks to `http://localhost:8045` directly, never through
  CMS). Setup uses the Keycloak admin REST API with the E2E admin credentials to create, per run:
  two confidential **subject** clients (service accounts; one additionally with "use refresh tokens
  for client credentials grant" enabled so a refresh token exists to characterize; one with a short
  access-token lifespan override for the expiry row), one confidential **observer** client used only
  for introspection (D-16; its credentials are never sent to the revoke endpoint), and one
  **public** client (`publicClient=true`, `directAccessGrantsEnabled=true`, plus a test user so it
  can hold a token). All names carry a per-run GUID; `[OneTimeTearDown]` deletes the clients and the
  user through the admin API, so nothing leaks into the shared realm. Nothing in
  `setup-keycloak.ps1` or the compose files changes.
- Evidence rows. Each row records the exact HTTP status, exact `error`, exact `error_description`,
  and, through the observer, the token's introspection state **after** the call. Rows that start
  from a live token (marked †) also record `active:true` **before**; rows whose input is by
  construction not live (unknown, expired, foreign-realm, ID token, no token) record only the
  observed response and, where a token exists, its unchanged state:
  owner revoke (access token) †; cross-client access token †; owner's token presented by a client
  whose id differs only by case †; invalid secret + unknown token; invalid secret + valid own token
  † (must stay active); unknown client id; no credentials †; Basic + form mixed † (which identity
  wins); duplicated `token` †; `token_type_hint` = `access_token` †, `refresh_token` †, `bogus` †,
  empty †; a refresh token † (if issued) and the effect on the paired access token; an ID token;
  an expired access token; a token signed by a different realm/key; a public client revoking its
  own user-flow token with no secret †, with an arbitrary secret †, and another client's token †;
  idempotent second revoke of an already-revoked token.
- Expiry row: the fixture reads `exp` from the token, then polls the observer's introspection at
  one-second intervals until it reports `active:false`, with a deadline of `exp + 60 s` (Keycloak's
  own clock skew is not assumed); it does not sleep a fixed interval and does not assume the
  configured lifespan was honored. Only once `active:false` has been observed is the revoke row
  executed.
- Assertions are **exact**: each row asserts the status and strings predicted in §2.1 (or `null`
  for "no prediction") so a deviation fails the fixture. A deviation is then recorded in §9.1 and
  the expectation is corrected in a follow-up commit after review; the fixture never asserts on
  membership in a set of allowed shapes. Preconditions are asserted first and fail loudly:
  `sslRequired` compatible with the in-network URL; `azp` present on service-account tokens; the
  observer introspects a subject's fresh token as `active:true`; the observer is refused nothing it
  needs (A-06).
- Behavior: none in production code.
- Tests/commands: `dotnet test src/config/tests/EdFi.DmsConfigurationService.Tests.E2E --filter "Category=KeycloakCharacterization"`
  with `DMS_CONFIG_IDENTITY_PROVIDER=keycloak` and the stack started with `-IdentityProvider keycloak`.
- Second run against `26.7` (Q-02, resolved: required as a compatibility record, not a support
  policy) on **isolated, disposable storage**: a scratch-directory compose override that changes the
  image reference **and** the volume name, started under its own project name
  (`-p cs-char-267`) so it never touches the baseline's `dms-keycloak` volume or the `cs-local`
  stack; realm setup is re-run against it with `setup-keycloak.ps1`; the project and its volume are
  removed (`down -v`) afterwards. Nothing is committed. Any difference on a row CMS depends on
  blocks P3.2 until resolved.
- Checkpoint: **approval of the §9.1 table and of any revision to D-10/D-11/D-16 it forces.**

### Phase 2 — Shared contract, self-contained safeguards, endpoint contract

Ordered so every commit builds, no commit weakens an existing protection, and the endpoint is
hardened **before** Keycloak delegation exists (so Keycloak lands behind D-05, not in front of it).

**P2.1 Move the contract; classify failures; map results at the endpoint.**
- Objective: D-02, D-07 (steps 1–7, still calling the one-argument repository method), D-13, 503
  mapping. ACs: AC5, AC4 (503 row), AC1.
- Files: add `Backend/ITokenRevocationManager.cs` (+ records); delete
  `Backend.OpenIddict/Token/ITokenRevocationManager.cs`; edit `OpenIddictTokenManager.cs`
  (replace `AuthenticateClientAsync`/`RevokeTokenAsync(string,string)` with the new method; add
  `LoadVerificationKeysAsync`); edit `IdentityModule.cs` (resolve `ITokenRevocationManager` with
  `GetService` — **optional in this step** so Keycloak mode keeps today's no-op 200 until P3.2;
  build the `TokenRevocationRequest`; map results; add 503 helper); edit the two engine extension
  `using`s; migrate `OpenIddictTokenManagerTests` and `IdentityModuleTests.RevocationOwnershipTests`
  to the new signature (same scenarios, same assertions, now asserting on result types). The
  optional resolve stays until P4.1 makes it required; Keycloak mode keeps today's no-op 200 until
  P3.2 registers its manager.
- Behavior change: operational failures become 503 instead of 200/500. Everything else as today.
- Risks: `JwtAuthenticationExtensions`/other callers of the old interface (grep shows none).
- Tests added: each D-07.4 row (database throw, corrupt key record, zero keys, unknown `kid`,
  certificate path misconfigured); application lookup throw → 503 at HTTP level; `UPDATE` throw →
  503; secret-hasher throw → 503; mutation-then-lost-response (fake repository completes the
  `UPDATE` callback then throws) → 503 and **no** assertion on token state; unknown token with bad
  credentials → `InvalidClient` and no key/token repository call; D-15 log-content assertions on
  the expired/untrusted/wrong-audience paths; a comparison-logic fault injected through a test
  subclass → exception propagates (500 at HTTP level), not 503.
- Commands: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit --filter "FullyQualifiedName~OpenIddictTokenManagerTests"`;
  `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit --filter "FullyQualifiedName~Revocation|FullyQualifiedName~OAuthEndpointErrorTests"`.
- Checkpoint.

**P2.2 Ownership-constrained mutation on both engines.**
- Objective: D-08. ACs: AC3, AC1, AC8.
- Files: `IOpenIddictTokenRepository.cs`, `IOpenIddictDataRepository.cs`, both
  `OpenIddictTokenRepository.cs`, both `OpenIddictDataRepository.cs`, `OpenIddictTokenManager.cs`
  (pass `applicationInfo.Id`), both `OpenIddictDataRepositoryTests.cs`, unit tests that fake
  `RevokeTokenAsync`.
- Tests: new `Given_RevokeTokenAsync_*` fixtures on PG and MSSQL: owning application → `revoked`
  with `RedemptionDate` set; other application → row untouched (`Status = 'valid'`), returns
  false; second revocation → false and `RedemptionDate` unchanged; unknown id → false. Existing
  sweep/concurrency tests updated to the two-argument call.
- Commands: `./build-config.ps1 IntegrationTest -Configuration Release` with a local PostgreSQL
  per `Configuration.cs`, and MSSQL via `ConnectionStrings__MssqlAdmin` (AGENTS.md known-good
  container on port 14333). Report skips explicitly.
- Checkpoint.

**P2.3 Endpoint request-shape validation, status rules, and the OAuth exception writer.**
- Objective: D-01, D-03, D-04, D-05, D-06, D-17. ACs: AC2, AC4.
- Files: `IdentityModule.cs` (new strict Basic parser, duplicate/header and mixed checks, 400/401
  rule, hint parsing, route marker metadata); new `Infrastructure/OAuthErrorContractMetadata.cs`
  and `Infrastructure/OAuthErrorResponseWriter.cs`; `GlobalExceptionHandler.cs` (marker branch
  only); `IdentityModuleTests.cs`; `GlobalExceptionHandlerTests.cs`; the two DMS-1218-era fixtures
  (`Given_a_revocation_request_without_a_token` → now asserts OAuth 400;
  `…from_an_unauthenticated_caller` → re-purposed: no credentials → 400 `invalid_client` with no
  challenge). `TokenRequest`/`/connect/token` untouched and its fixtures re-run unchanged.
- Tests: one fixture per D-03 row, every D-03 worked example, every D-04 decoding case, and the
  precedence pairs: (missing token + bad credentials) → 400 `invalid_request`; (mixed + valid
  Basic) → 400 `invalid_request`, manager never called; (malformed Basic + form credential keys)
  → 400 `invalid_request`, manager never called; (malformed Basic alone) → 401 + challenge, manager
  never called; (valid credentials + unknown token + repository outage) → 503; (`Bearer` header +
  form credentials) → form rules; hint `bogus`/empty → `None`; D-17 pipeline fixtures (manager
  throws → 500 OAuth JSON and logged as failed; malformed multipart → 400 OAuth JSON;
  `/connect/token` still Ed-Fi for both).
- E2E, new `Revocation.feature` (Q-05, resolved), untagged so it runs on **both** providers:
  missing token → 400 OAuth; mixed → 400; no credentials → 400 `invalid_client`; wrong Basic
  secret → 401 with `WWW-Authenticate`; owner revoke authenticated with **Basic** → 200 and
  introspection `active:false`; owner revoke authenticated with **form** credentials → same;
  self-contained cross-client: second client created via `/v3/apiClients` with credentials
  captured, first client's token introspected `active:true`, revoke attempt by the second client →
  200, protected resource still 200 **and** introspection still `active:true`. The introspection
  step is provider-aware (CMS `/connect/introspect` for self-contained, Keycloak's endpoint for
  keycloak, D-16). Until P3.2 the Keycloak lane of this feature is expected to fail on the
  authentication rows (Keycloak mode still short-circuits to 200); the step report states this,
  and the self-contained lane must be fully green.
- Commands: frontend unit filter as above;
  `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained`
  (**no** `-SkipDockerBuild`: production code changed, so the image is rebuilt via `-r`; §6.8).
- Risks: DMS-1218 test expectations change for this endpoint only (documented in D-01); the
  `+` decoding difference between `/connect/revoke` and `/connect/token` (D-04) is deliberate and
  goes in the release note.
- Checkpoint.

### Phase 3 — Keycloak integration

Phase 3 is split so that **no commit registers a Keycloak revocation manager without the
public-client gate**. P3.1 is code plus unit tests with no DI registration and therefore no
executable path from the endpoint; P3.2 activates it.

**P3.1 Keycloak revocation manager: delegation, mapping, and the public-client gate (not registered).**
- Objective: D-09, D-10 using the §9.1 strings, D-11 (all of it), D-15. ACs: AC2, AC3, AC4, AC5, AC6.
- Preconditions: P1.1 approved; P2.3 committed locally (endpoint rejects mixed/duplicates before
  anything is delegated).
- Files: new `Backend.Keycloak/KeycloakTokenRevocationManager.cs`; `IKeycloakClientFacade.cs` +
  `KeycloakClientFacade.cs` (`GetClientsByClientIdAsync` with the D-11.4 timeout wrapper); new
  `Backend.Tests.Unit/KeycloakTokenRevocationManagerTests.cs` using a recording `HttpMessageHandler`
  behind a faked `IHttpClientFactory` (pattern: `MetadataModuleTests.RecordingHandler`) and a
  faked facade. **No change** to `KeycloakServiceExtensions.cs` or `IdentityModule.cs`; in Keycloak
  mode the endpoint still short-circuits exactly as before this ticket.
- A-01 is resolved here: if the `Client` model lacks `PublicClient`, the facade performs the admin
  `GET /admin/realms/{realm}/clients?clientId=` itself with the admin token `Keycloak.Net` already
  obtains, deserializing only `clientId`, `publicClient`, `bearerOnly` with `System.Text.Json`. The
  choice is reported at the checkpoint, not made silently.
- Tests: request assertions (URL, form fields, caller credentials only and never
  `KeycloakContext.ClientSecret`, hint forwarding per D-06, `Content-Type`, exactly one request);
  every D-10 row keyed on the §9.1 strings; every D-11.3 row, each asserting whether a revoke
  request was sent (only the first row sends one); D-11.4 timeout vs caller cancellation for the
  admin read and the revoke call; connection error; 5xx; non-JSON body on 400; oversize body;
  mutation-then-lost-response (handler records the revoke request, then throws while the response
  body is read) → 503 with no state assertion; D-15 log-content fixtures.
- Commands: `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit --filter "FullyQualifiedName~KeycloakTokenRevocationManagerTests"`.
- Checkpoint.

**P3.2 Activate Keycloak revocation and verify end to end.**
- Objective: D-12 registration half, D-16 for Keycloak. ACs: AC2, AC3, AC6, AC8.
- Preconditions: P3.1 approved; §9.1 shows no unresolved 26.1/26.7 difference on a dependent row.
- Files: `KeycloakServiceExtensions.cs` (register `ITokenRevocationManager`); `SetupHooks.cs`
  (`@KeycloakOnly` hook mirroring `SelfContainedOnly`; a feature-scoped observer client created
  through the admin API in `[BeforeFeature]` for `Revocation.feature` under Keycloak and deleted in
  `[AfterFeature]`); `StepDefinitions.cs` (steps: introspect a named token at Keycloak **as the
  observer** (D-16) and assert `active:true`/`active:false`; create a public client with a user
  through the admin API, obtain its token, attempt revocation with no secret and with an arbitrary
  secret; delete the public client and user in `[AfterScenario]`). Observer credentials are held
  in a separate slot and are never used by the revoke step;
  `Revocation.feature` (`@KeycloakOnly` scenarios: owner revoke → introspect `active:false`;
  cross-client → 200 and introspect `active:true`; public client, arbitrary secret → 400
  `invalid_client` and its token introspects `active:true`; public client, no secret → 400
  `invalid_client`). The untagged P2.3 scenarios now pass on the Keycloak lane. Scenario 18 keeps
  `@SelfContainedOnly` (D-16). Owner-revoke scenarios are tagged `@MssqlRepresentative` so the
  MSSQL lane exercises the constrained `UPDATE` end to end.
- Commands (image rebuilt, §6.8):
  `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider keycloak`;
  `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider self-contained`;
  `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider keycloak -EnvironmentFile ./.env.config.mssql.e2e -E2ETestFilter "TestCategory=MssqlRepresentative"`
  and the same with `self-contained`.
- Checkpoint.

### Phase 4 — Startup validation

**P4.1 Required injection and startup registration check.**
- Objective: D-12. ACs: AC7.
- Files: `Program.cs` (`EnsureTokenRevocationSupport`), `IdentityModule.cs` (required
  `[FromServices]`), new `Frontend…Tests.Unit/Configuration/TokenRevocationStartupTests.cs`
  (pattern: `DatabaseOptionsStartupTests` + `CmsPluginBoot` settings override): boots
  self-contained/postgresql, self-contained/mssql, keycloak; each succeeds; each with
  `ConfigureTestServices(s => s.RemoveAll<ITokenRevocationManager>())` fails to start with the
  D-12 "not registered" message; each with a registration whose factory throws fails to start
  with the D-12 "could not be constructed" message naming the exception type chain, the thrown `InvalidOperationException` carrying no inner exception and the captured critical log carrying no exception object (D-12, D-15 rule 1); the
  Keycloak boots run with no reachable Keycloak and a recording handler proving no request was
  issued; a runtime fixture proves a 503 from a faked manager leaves the host serving the next
  request.
- `docs/changelog/8.1.0.md` (Q-06, resolved): amend the existing DMS-1478 entry so the completed
  prerequisite (self-contained authentication and ownership) is distinguished from this ticket's
  additions (Keycloak support, OAuth error format for every revocation error, 503 vs refused
  startup, `+` decoding on `/connect/revoke`).
- Checkpoint.

### Phase 5 — Final verification and documentation

**P5.1 Documentation and full verification.**
- Files: `CS-AUTH.md` (the Q-09 plugin compatibility requirement for replacement
  `IClientSecretHasher` implementations; the D-07.6 validator residual limitation; and rewrite the
  revocation sections: both providers authenticated; error format
  exception to DMS-1218; token types per provider; related-token/session effects; validation paths;
  propagation/clock-skew limits; TLS: delegation goes to `IdentitySettings:Authority`, which must
  be HTTPS outside local development; local `http://dms-keycloak:8080` is the documented exception;
  retry and confirmation guidance for 503 per D-13.3; the supported-token-type matrix of D-16 with
  "untested" rows marked as such; the 26.1 baseline and 26.7 compatibility statement of Q-02),
  `OWASP-AUTH-COVERAGE.md` (remove the "deliberate gap" paragraph; describe the new posture),
  `KEYCLOAK-SETUP.md` (realm `sslRequired` note and the `view-clients` permission the service
  account needs for the client-type read), `reference/design/configuration-service/README.md`
  (link this doc), this document (§9 evidence; known limitations: the admin read cannot be aborted
  on timeout, one admin read per revocation, `+` decoding differs from `/connect/token`).
- Full runs: config unit tests, integration tests on PG and MSSQL, E2E on PG × {keycloak,
  self-contained} and MSSQL representative × both, `dotnet csharpier check src/config`.
- Deliverable: final commit list, cumulative file summary, AC evidence matrix (§9.2), limitations.
- Checkpoint: **push approval.**

### 6.7 Dependencies summary

P1.1 → P3.1 (evidence strings) and P3.2 (no unresolved version difference). P2.1 → P2.2, P2.3
(contract). P2.3 → P3.1 (endpoint hardened before any delegation exists). P3.1 → P3.2 (gate exists
before registration). P3.2 → P4.1 (both providers register, so "required" is safe). P4.1 → P5.1.

### 6.8 Running E2E against the reviewed code (applies to every E2E command above)

`build-config.ps1 E2ETest` only rebuilds the Configuration Service image when `-SkipDockerBuild`
is **absent** (it then calls `start-local-config.ps1 … -r`). Compiling locally does not update the
container. Rules for this ticket:

1. After any production change, run E2E **without** `-SkipDockerBuild`. `-SkipDockerBuild` is
   allowed only when re-running tests against an image already built from the exact commit under
   review (for example to run the second provider lane).
2. Every E2E result in a step report and in §9.2 records: the git SHA the stack was built from
   (`git rev-parse HEAD` with `git status --short` empty at build time), and the image identity the
   stack ran (`docker inspect ed-fi-api-config-service --format '{{.Image}} {{.Created}}'`), so the
   evidence is tied to the reviewed code.
3. Order within a step: (a) unit and integration tests run on the working tree while the change is
   being made; (b) the step is committed; (c) E2E runs against the stack built from that clean
   commit and the SHA/image pair is recorded; (d) if E2E fails, the fix is a **follow-up commit**
   (reviewed commits are not rewritten) and (c) is repeated against the new commit. The final
   verification (P5.1) runs every lane against the final commit of the branch and records it.
4. Teardown (`teardown-local-cms.ps1`) between provider lanes, because the stacks differ in realm
   setup and the test data cleanup does not remove identity-provider clients.

---

## 7. Assumptions and open decisions

Assumptions still open (each is closed at the named step and reported at its checkpoint):

| ID | Item | Closed by |
|---|---|---|
| A-01 | **Closed by P3.1.** `Keycloak.Net.Core` 1.0.29 `Client` exposes `bool? PublicClient` and `bool? BearerOnly`, and `GetClientsAsync(realm, clientId, viewableOnly, cancellationToken)` takes both a `clientId` filter and a `CancellationToken`. The package deserializes with `System.Text.Json` through Flurl 4.0.2, so a member Keycloak omits stays `null`; facade tests run the real package against Flurl's test transport to pin this. The facade uses the package call; no admin GET of its own. See §9.3 for the synchronous admin-token fetch this uncovered | P3.1 |
| A-02 | The E2E realm allows HTTP from the Docker network (`sslRequired` ≠ `all`); otherwise every delegated call is 403. | P1.1 precondition assertion |
| A-03 | **Closed by P3.1.** The stalled-handler test observes `TaskCanceledException -> TimeoutException` for an `HttpClient` timeout on .NET 10. The manager does not depend on the inner type: any cancellation while the caller's token is not cancelled is the timeout. | P3.1 stalled-handler test |
| A-04 | Keycloak client-credentials access tokens carry `azp` equal to the client id (needed for Keycloak's own ownership check). | P1.1 precondition assertion |
| A-05 | `IExceptionHandlerFeature.Endpoint` carries the route's metadata when `GlobalExceptionHandler` runs for an exception thrown inside a Minimal API handler (it already relies on this for route values). | **Closed by P2.3**: the pipeline fixtures (malformed multipart body, faulting manager) receive the OAuth body, and fail when the route marker is removed |
| A-06 | **Corrected by P1.1 (§9.1.4).** Keycloak refuses public clients at introspection (403 `invalid_request` "Client not allowed.", observed on 26.1.4 and 26.7.5). Access tokens: on 26.1.4 any confidential client can introspect any token; on 26.7.5 the introspecting client must be in the token's `aud` or the answer is `active:false` (the Red Hat build of Keycloak 26.4 migration guide attributes this check to 26.4.12; upstream versions between 26.1.4 and 26.7.5 were not observed). Refresh tokens: introspectable only by the client they were issued to, with `token_type_hint=refresh_token` (26.7.5 `RefreshTokenIntrospectionProvider` compares the authenticated client with the token's client; 26.1.4 also answers the owner). | Closed by P1.1 |

Decisions resolved by the revision-1 review (recorded so later steps do not reopen them):

| ID | Decision |
|---|---|
| Q-01 | Keep the administrative client-type gate, with the D-11.3 fail-closed table and D-11.4 timeout/cancellation rules. |
| Q-02 | The pinned `26.1` image is the required baseline; a `26.7` compatibility run is recorded in §9.1. This is not an official "26.1 only" support policy. Any mapping difference is resolved before P3.2 activates delegation. |
| Q-03 | A non-Basic `Authorization` header is ignored; form credentials must authenticate on their own; a `Bearer` header grants no authority. |
| Q-04 | Unexpected programming faults are 500 with the fixed OAuth JSON `server_error` body (D-17), never 503 and never Ed-Fi JSON on this endpoint. |
| Q-05 | Scenarios live in a focused `Revocation.feature`. |
| Q-06 | Changelog `8.1.0.md`; the completed DMS-1478 prerequisite is distinguished from this ticket's additions. |
| Q-07 | `invalid_client` for "client not found" only after an authoritative successful admin read; a failed, refused or ambiguous read is 503. |
| Q-09 | (raised in the first P2.1 correction, resolved by review) **Plugin compatibility requirement.** The `IClientSecretHasher` replace contract is unchanged and no member is added: a default interface method wrapping `VerifySecretAsync` could not recover a failure the plugin already suppressed. A replacement hasher's `VerifySecretAsync` must (1) return `false` for normal credential rejection, (2) **throw** for an operational failure that prevents verification (configuration, dependency, unreadable or incomplete stored value), and (3) log neither secret nor dependency exception content. Revocation calls a conforming plugin through `VerifySecretAsync` and its boundary classifies the exception as `TemporarilyUnavailable`. A plugin that suppresses operational failures **cannot satisfy** the revocation guarantee: its failures are answered `invalid_client`. That outcome mutates nothing, but preventing mutation alone does **not** satisfy AC5's separation of operational failures from authentication outcomes, so such a plugin is non-conforming. The built-in hasher's `VerifySecretAsync` predates this requirement and suppresses failures, which is why revocation uses its `IFailurePreservingSecretVerifier` path instead. The `Secrets` package is separately versioned (1.0.0), so the requirement is recorded here and published with the plugin-facing documentation in P5.1, not by editing the shipped contract. |
| Q-08 | (raised and resolved in P1.1) The characterization fixture stays in the existing Keycloak CI E2E lane as a regression guard on the pinned image; no opt-in variable hides it. Every prediction is exact, and the only skipped checks are state checks that do not exist for a row (before-state of a request without a live token, after-state of a request without a token or with a foreign-realm token). |

§2.1 rows marked "observed in P1.1" and every behaviour recorded in §9.1 are observations from
the pinned 26.1.4 image (and from 26.7.5 for the compatibility record). The remaining §2.1 rows
that P1.1 did not exercise are still source-derived: bearer-only clients ("Bearer-only not
allowed"), a token whose `azp` is missing, a session or user that cannot be resolved, and the
offline-token effect. They stay "expected from source" until a step exercises them, and no
normalization keys on them (D-10 keys only on strings observed in §9.1).

---

## 8. Adversarial review checklist (for Codex)

Please challenge each item and cite the decision or step that would fail:

1. Does the final behavior (§4, D-01…D-16) satisfy every Jira AC and every response-contract row?
   Is any row answered by the wrong layer (endpoint vs manager)?
2. Does any phase defer, weaken, reinterpret or omit an AC? In particular: is AC6's "before
   implementing normalization" really enforced by P1.1 → P3.1 ordering?
3. Are the DMS-1478 protections preserved verbatim: signature/issuer/audience/lifetime check,
   ordinal ownership comparison, canonical client id on both sides, MSSQL vs PostgreSQL lookup
   differences, every existing security test migrated rather than dropped?
4. Can malformed, partial or mixed credentials bypass authentication? Check D-03 rows 3–7 against:
   `Basic` with empty secret; form `client_id` only; Basic + form `client_secret` only; duplicated
   `client_id` where one copy is valid; a `Bearer` header plus form credentials (Q-03).
5. Can a public client mutate token state before rejection under D-11, including when the admin
   read fails, times out, or returns "not found"? Is the ordering (shape → gate → revoke) enforced
   by tests that assert no revoke request was sent?
6. Can an invalid or unknown token hide an authentication failure or an infrastructure failure?
   Check D-07 steps 1–4 and D-10 rows 1, 2 and 6: is there any path where a 200 is returned without
   the caller having authenticated, or where a database/provider error is answered 200?
7. Is database ownership enforced **in the mutation** on both engines (D-08), not only by the
   preceding claim comparison? Would a forged `jti` naming the caller still be stopped if the claim
   comparison were removed?
8. Is the Keycloak normalization (D-10 row 2) supported only by P1.1 evidence and narrow enough
   (both `error` and `error_description` exact)? Is any other `invalid_request` normalized? Is any
   provider body forwarded or logged?
9. Do the tests prove active-before/revoked-after state (D-16) on **both** providers and unchanged
   state for cross-client and public-client attempts, through the documented validation path and
   not through the 200 alone?
10. Are startup registration failure (D-12: refused boot, nonzero exit, fixed message, no provider
    contact) and runtime unavailability (D-13: 503, host keeps serving) demonstrably different, each
    with its own test?
11. Is the scope minimal? Flag anything that touches `/connect/token`, introspection, DMS token
    validation, tenant routing, schema, collation, bulk revocation or PKCE.
12. Is any statement in §2.1 being treated as fact before P1.1? Is any assumption in §7 silently
    load-bearing?
13. Revision-2 specifics: does any commit in §6 register a Keycloak revocation manager without the
    D-11 gate? Does D-11.3 leave any lookup outcome undefined, or let a non-authoritative read
    produce `invalid_client`? Does D-15 leave any path that logs a raw provider value or an
    exception message carrying caller input? Does D-17 preserve 500 logging and keep the marker
    off every other route? Does any E2E command in §6 run with `-SkipDockerBuild` after a
    production change?
14. Revision-3 specifics: can any log record on the revocation or startup path carry an exception
    object, a verification `Detail`, or a provider value (D-07.5, D-12, D-15 rules 1–4)? Do the
    disclosure tests walk state, scopes, the attached exception and its inner chain? Is every
    Keycloak before/after observation made by the observer client and never by credentials that
    were submitted to revocation? Does D-04 validate base64 alphabet/padding, percent escapes and
    strict UTF-8 after percent decoding explicitly rather than trusting `Convert.FromBase64String`
    or `WebUtility.UrlDecode`? Is the 26.7 run isolated from the baseline's storage?

---

## 9. Evidence (filled in during implementation; empty until then)

### 9.1 Keycloak characterization results (P1.1)

Recorded 2026-10-01 from the P1.1 fixture
(`src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/Keycloak/KeycloakRevocationCharacterizationTests.cs`,
helpers in `KeycloakCharacterizationHarness.cs`, offline disclosure tests in
`KeycloakCharacterizationDiagnosticsTests.cs`). Nothing in production code, `setup-keycloak.ps1` or
the compose files changed. The first P1.1 commit (`7cebf3660`) recorded the raw observations with the
§2.1 predictions as they stood; the P1.1 correction commit applied the reviewed corrections (§0.1) and
re-ran both versions. The tables below are from the corrected run: every prediction is exact and every
row passes on both versions.

#### 9.1.1 Environment and provenance

| Item | Baseline (required, CI image) | Compatibility run |
|---|---|---|
| Image reference | `quay.io/keycloak/keycloak:26.1@sha256:044a457e04987e1fff756be3d2fa325a4ef420fa356b7034ecc9f1b693c32761` (`eng/docker-compose/keycloak.yml`, unchanged) | `quay.io/keycloak/keycloak:26.7` (the `eng/azure-vm` reference), resolved to `sha256:37dbaf6f0722c9ec246335f36e1ef8b2e6cb960f7c27e0d8c615121a3d475a85` |
| Image id / label `version` | `sha256:044a457e…` / `26.1.4` | `sha256:37dbaf6f…` / `26.7.5` |
| Server version reported by `GET /admin/serverinfo` | `26.1.4` | `26.7.5` |
| Container, port, project | `dms-keycloak`, `127.0.0.1:8045`, compose project `cs-local` | `cs-char-267-keycloak`, `127.0.0.1:8046`, compose project `cs-char-267`, own volume `cs-char-267-keycloak` and own default network; a standalone compose file in the session scratch directory, **not** an override of `keycloak.yml`, so no container name, port, volume or network is shared with `cs-local`; removed afterwards with `docker compose -p cs-char-267 -f <scratch>/compose.yml down -v --rmi all` |
| Realm | `edfi`, `sslRequired` = `external`, created by `setup-keycloak.ps1` ×3 with the parameters `start-local-config.ps1` passes (lines 197–207) | same script, same parameters, `-KeycloakServer http://localhost:8046` |
| CMS container (started, not exercised) | `ed-fi-api-config-service`, image `sha256:5e6b05b1910b9673068038a76a6fbf7f5aa7e76326e74340c4446b5c706c1e92`, built with `-r` from `f3a44e12b`, `AppSettings__IdentityProvider=keycloak`, `IdentitySettings__Authority=http://dms-keycloak:8080/realms/edfi` | none |

Environment note for the reviewer: when the first P1.1 commit was produced, PowerShell 7 (`pwsh`)
was not installed on the machine, so `start-local-config.ps1 -EnvironmentFile ./.env.config.e2e -r
-IdentityProvider keycloak -AddE2EClaimSets` ran under Windows PowerShell 5.1: the claim sets were
staged and `docker compose … up` succeeded with the Keycloak provider, but the script aborted before
its realm setup (`database-safety.psm1` uses `??`). The three `setup-keycloak.ps1` invocations it
would have made (lines 197–207) were run by hand with identical parameters. PowerShell 7.6.6 has since
been installed and both compose modules load under it. The realm was not recreated for the
correction run; it is the same `edfi` realm.

Commands (from the repository root; `DMS_CONFIG_IDENTITY_PROVIDER=keycloak` and `KEYCLOAK_PORT` set in
the test process for the characterization runs, neither set for the offline diagnostics run):

```
dotnet build src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/EdFi.DmsConfigurationService.Tests.E2E.csproj -c Release
dotnet test  src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/EdFi.DmsConfigurationService.Tests.E2E.csproj -c Release --no-build --filter "FullyQualifiedName~Tests.E2E.Keycloak&Category!=KeycloakCharacterization"
dotnet test  src/config/tests/EdFi.DmsConfigurationService.Tests.E2E/EdFi.DmsConfigurationService.Tests.E2E.csproj -c Release --no-build --filter "Category=KeycloakCharacterization"
```

| Run | Tests | Passed | Failed | Skipped | Wall time |
|---|---|---|---|---|---|
| Offline diagnostics (no Keycloak) | 20 | 20 | 0 | 0 | 2 s |
| 26.1.4 (`KEYCLOAK_PORT=8045`) | 154 | 146 | 0 | 8 | 16 s |
| 26.7.5 (`KEYCLOAK_PORT=8046`) | 154 | 146 | 0 | 8 | 17 s |

The 8 skips are identical in both runs and are state checks that do not exist for the row, each
reported as "Not applicable": the before-state of K-04, K-16, K-17, K-21 and K-22 (no live token at
the start of the call) and the after-state of K-04, K-17 and K-22 (no token, or a foreign-realm token
with no observable state in the `edfi` realm). No prediction is left open.

Each run also wrote its table to `bin/Release/net10.0/keycloak-characterization-<version>.md`; the
tables below are those files verbatim. The fixture deleted its clients, user and client scope at the
end of every run; both realms were checked afterwards through the admin API and hold no `cs-char-*`
client, user or client scope.

#### 9.1.2 Keycloak 26.1.4 (baseline)

| ID | Scenario | HTTP | Body | `error` | `error_description` | active before | active after | Notes |
|---|---|---|---|---|---|---|---|---|
| K-00 | Preconditions (A-02, A-04, A-06 as corrected) | — | — | — | — | — | — | server 26.1.4; sslRequired=external; azp=client id; observer (in aud via the per-run scope) sees subject access token active and public user access token active; refresh subject sees its own refresh token (hint refresh_token) active; public user refresh grant probe HTTP 200; refresh token issued for client_credentials: yes |
| K-01 | Owner revokes its own access token (form credentials) † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | client_secret_post |
| K-01b | Owner revokes its own access token (HTTP Basic) † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | client_secret_basic |
| K-02 | Cross-client: B presents A's access token † | 400 / 400 | OAuth JSON / JSON object, application/json, 68 chars | `invalid_request` / `invalid_request` | "Unmatching clients" / "Unmatching clients" | active | active / active | B authenticated with its own correct secret |
| K-03 | Owner's token presented with the owner's secret under a case-variant client_id † | 401 / 401 | OAuth JSON / JSON object, application/json, 93 chars | `invalid_client` / `invalid_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | active | active / active | client_id upper-cased; answered as an unknown client |
| K-04 | Invalid secret + unknown token | 401 / 401 | OAuth JSON / JSON object, application/json, 98 chars | `unauthorized_client` / `unauthorized_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | n/a | n/a / n/a | no live token involved |
| K-05 | Invalid secret + the caller's own valid token † | 401 / 401 | OAuth JSON / JSON object, application/json, 98 chars | `unauthorized_client` / `unauthorized_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | active | active / active | token must stay active |
| K-06 | Unknown client_id presenting A's live token | 401 / 401 | OAuth JSON / JSON object, application/json, 93 chars | `invalid_client` / `invalid_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | active | active / active | A's token is live only to prove it is left untouched |
| K-07 | No credentials at all, A's live token † | 401 / 401 | OAuth JSON / JSON object, application/json, 93 chars | `invalid_client` / `invalid_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | active | active / active | no Authorization header, no client_id, no client_secret |
| K-08 | Mixed mechanisms: Basic = A, form = B, token = A's † | 400 / 400 | OAuth JSON / JSON object, application/json, 68 chars | `invalid_request` / `invalid_request` | "Unmatching clients" / "Unmatching clients" | active | active / active | identity that Keycloak used: form (B) won |
| K-09 | Duplicated `token` parameter (same live token twice) † | 400 / 400 | OAuth JSON / JSON object, application/json, 70 chars | `invalid_request` / `invalid_request` | "duplicated parameter" / "duplicated parameter" | active | active / active | owner credentials |
| K-10 | Owner revokes its access token with token_type_hint="access_token" † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | the endpoint source does not read the hint |
| K-11 | Owner revokes its access token with token_type_hint="refresh_token" † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | the endpoint source does not read the hint |
| K-12 | Owner revokes its access token with token_type_hint="bogus" † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | the endpoint source does not read the hint |
| K-13 | Owner revokes its access token with token_type_hint="" † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | the endpoint source does not read the hint |
| K-14 | Owner revokes its refresh token (client_credentials with refresh enabled); refresh state seen by the owner, paired access token by the observer † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | observer's view of the paired access token: active before, inactive after |
| K-15 | ID token (public user's, scope=openid) presented by confidential A; state is the paired access token's | 400 / 400 | OAuth JSON / JSON object, application/json, 79 chars | `unsupported_token_type` / `unsupported_token_type` | "Unsupported token type" / "Unsupported token type" | active | active / active | A is not the ID token's azp, so a 400 unsupported_token_type here shows the type check runs before the ownership check |
| K-16 | Owner revokes its own naturally expired access token (short-lifespan client, expiry observed first) | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | n/a | inactive / inactive | active at issue: True; token lifespan exp-iat=10s; observer reported inactive after 12 polls (11s); configured lifespan 10s |
| K-17 | Token issued and signed by the master realm, presented by confidential A | 200 / 200 | OAuth JSON / JSON object, application/json, 61 chars | `invalid_token` / `invalid_token` | "Invalid token" / "Invalid token" | n/a | n/a / n/a | a master-realm token has no observable state in the edfi realm |
| K-18 | Public client revokes its own user-flow access token, no secret † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | refresh grant probe with the paired refresh token after the call: HTTP 200 |
| K-19 | Public client revokes its own user-flow access token with an arbitrary secret † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | client_secret ignored for a public client per §2.1 |
| K-20 | Public client (no secret) presents confidential A's access token † | 400 / 400 | OAuth JSON / JSON object, application/json, 68 chars | `invalid_request` / `invalid_request` | "Unmatching clients" / "Unmatching clients" | active | active / active | public client authenticates, then fails Keycloak's ownership check |
| K-21 | Idempotent second revoke of an already revoked access token | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | n/a | inactive / inactive | first call: HTTP 200, token inactive afterwards |
| K-22 | Owner credentials, no `token` parameter | 400 / 400 | OAuth JSON / JSON object, application/json, 68 chars | `invalid_request` / `invalid_request` | "Token not provided" / "Token not provided" | n/a | n/a / n/a | no token involved |
| K-23 | Introspection endpoint called by the public client with its own user token (observer rationale, A-06) | 403 / 403 | OAuth JSON / JSON object, application/json, 69 chars | `invalid_request` / `invalid_request` | "Client not allowed." / "Client not allowed." | active | active / active | not a revocation call; shows why a confidential observer is required |

#### 9.1.3 Keycloak 26.7.5 (compatibility record)

| ID | Scenario | HTTP | Body | `error` | `error_description` | active before | active after | Notes |
|---|---|---|---|---|---|---|---|---|
| K-00 | Preconditions (A-02, A-04, A-06 as corrected) | — | — | — | — | — | — | server 26.7.5; sslRequired=external; azp=client id; observer (in aud via the per-run scope) sees subject access token active and public user access token active; refresh subject sees its own refresh token (hint refresh_token) active; public user refresh grant probe HTTP 200; refresh token issued for client_credentials: yes |
| K-01 | Owner revokes its own access token (form credentials) † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | client_secret_post |
| K-01b | Owner revokes its own access token (HTTP Basic) † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | client_secret_basic |
| K-02 | Cross-client: B presents A's access token † | 400 / 400 | OAuth JSON / JSON object, application/json, 68 chars | `invalid_request` / `invalid_request` | "Unmatching clients" / "Unmatching clients" | active | active / active | B authenticated with its own correct secret |
| K-03 | Owner's token presented with the owner's secret under a case-variant client_id † | 401 / 401 | OAuth JSON / JSON object, application/json, 93 chars | `invalid_client` / `invalid_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | active | active / active | client_id upper-cased; answered as an unknown client |
| K-04 | Invalid secret + unknown token | 401 / 401 | OAuth JSON / JSON object, application/json, 98 chars | `unauthorized_client` / `unauthorized_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | n/a | n/a / n/a | no live token involved |
| K-05 | Invalid secret + the caller's own valid token † | 401 / 401 | OAuth JSON / JSON object, application/json, 98 chars | `unauthorized_client` / `unauthorized_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | active | active / active | token must stay active |
| K-06 | Unknown client_id presenting A's live token | 401 / 401 | OAuth JSON / JSON object, application/json, 93 chars | `invalid_client` / `invalid_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | active | active / active | A's token is live only to prove it is left untouched |
| K-07 | No credentials at all, A's live token † | 401 / 401 | OAuth JSON / JSON object, application/json, 93 chars | `invalid_client` / `invalid_client` | "Invalid client or Invalid client credentials" / "Invalid client or Invalid client credentials" | active | active / active | no Authorization header, no client_id, no client_secret |
| K-08 | Mixed mechanisms: Basic = A, form = B, token = A's † | 400 / 400 | OAuth JSON / JSON object, application/json, 68 chars | `invalid_request` / `invalid_request` | "Unmatching clients" / "Unmatching clients" | active | active / active | identity that Keycloak used: form (B) won |
| K-09 | Duplicated `token` parameter (same live token twice) † | 400 / 400 | OAuth JSON / JSON object, application/json, 70 chars | `invalid_request` / `invalid_request` | "duplicated parameter" / "duplicated parameter" | active | active / active | owner credentials |
| K-10 | Owner revokes its access token with token_type_hint="access_token" † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | the endpoint source does not read the hint |
| K-11 | Owner revokes its access token with token_type_hint="refresh_token" † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | the endpoint source does not read the hint |
| K-12 | Owner revokes its access token with token_type_hint="bogus" † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | the endpoint source does not read the hint |
| K-13 | Owner revokes its access token with token_type_hint="" † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | the endpoint source does not read the hint |
| K-14 | Owner revokes its refresh token (client_credentials with refresh enabled); refresh state seen by the owner, paired access token by the observer † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | observer's view of the paired access token: active before, inactive after |
| K-15 | ID token (public user's, scope=openid) presented by confidential A; state is the paired access token's | 400 / 400 | OAuth JSON / JSON object, application/json, 79 chars | `unsupported_token_type` / `unsupported_token_type` | "Unsupported token type" / "Unsupported token type" | active | active / active | A is not the ID token's azp, so a 400 unsupported_token_type here shows the type check runs before the ownership check |
| K-16 | Owner revokes its own naturally expired access token (short-lifespan client, expiry observed first) | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | n/a | inactive / inactive | active at issue: True; token lifespan exp-iat=10s; observer reported inactive after 12 polls (11s); configured lifespan 10s |
| K-17 | Token issued and signed by the master realm, presented by confidential A | 200 / 200 | OAuth JSON / JSON object, application/json, 61 chars | `invalid_token` / `invalid_token` | "Invalid token" / "Invalid token" | n/a | n/a / n/a | a master-realm token has no observable state in the edfi realm |
| K-18 | Public client revokes its own user-flow access token, no secret † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | refresh grant probe with the paired refresh token after the call: HTTP 200 |
| K-19 | Public client revokes its own user-flow access token with an arbitrary secret † | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | active | inactive / inactive | client_secret ignored for a public client per §2.1 |
| K-20 | Public client (no secret) presents confidential A's access token † | 400 / 400 | OAuth JSON / JSON object, application/json, 68 chars | `invalid_request` / `invalid_request` | "Unmatching clients" / "Unmatching clients" | active | active / active | public client authenticates, then fails Keycloak's ownership check |
| K-21 | Idempotent second revoke of an already revoked access token | 200 / 200 | empty / empty | (absent) / (absent) | (absent) / (absent) | n/a | inactive / inactive | first call: HTTP 200, token inactive afterwards |
| K-22 | Owner credentials, no `token` parameter | 400 / 400 | OAuth JSON / JSON object, application/json, 68 chars | `invalid_request` / `invalid_request` | "Token not provided" / "Token not provided" | n/a | n/a / n/a | no token involved |
| K-23 | Introspection endpoint called by the public client with its own user token (observer rationale, A-06) | 403 / 403 | OAuth JSON / JSON object, application/json, 69 chars | `invalid_request` / `invalid_request` | "Client not allowed." / "Client not allowed." | active | active / active | not a revocation call; shows why a confidential observer is required |

The two tables differ only in the server version noted in K-00.

#### 9.1.4 Findings and the corrections they caused

**Confirmed exactly as §2.1 predicted, on both versions:** success is `200` with a zero-length body
(K-01, K-01b, K-10…K-14, K-16, K-18, K-19, K-21); ownership mismatch is `400` `invalid_request` /
`Unmatching clients` with the token untouched (K-02, K-08, K-20) — this is the string D-10 row 2
normalizes, copied from the response body; duplicated parameter is `400` `invalid_request` /
`duplicated parameter` (K-09); missing token is `400` `invalid_request` / `Token not provided` (K-22);
an ID token is `400` `unsupported_token_type` / `Unsupported token type` and the check runs **before**
the ownership check (K-15 presents a token whose `azp` is not the caller and still gets the type
error) — D-10 row 3's "no ownership information" claim holds; a token from another realm is `200`
`invalid_token` / `Invalid token` (K-17); a public client authenticates with no secret and with an
arbitrary secret and revokes its own token (K-18, K-19) — D-11 remains necessary; the public client
cannot introspect (`403` `invalid_request` / `Client not allowed.`, K-23); the form `client_id` wins
over the Basic header (K-08); `token_type_hint` values `access_token`, `refresh_token`, `bogus` and
empty make no difference (K-10…K-13); a second revocation of a revoked token is `200` empty (K-21);
revoking a refresh token answers `200` empty and invalidates both the refresh token (owner's
introspection) and the paired access token (observer's introspection) (K-14); revoking an access
token leaves the session alone — the paired refresh token still buys a new access token (K-18). Every
OAuth error body is a JSON object served as `application/json`.

**§2.2 unknowns, now observed (both versions):**

| Unknown | Observation |
|---|---|
| Expired but correctly signed token | `session.tokens().decode` **accepts** it: the owner's expired token reaches the revocation path and answers `200` with an empty body, not `200` `invalid_token` (K-16; lifespan 10 s honored, observer reported inactive 11 s after issue). Both outcomes are CMS 200 under D-13; no design change |
| Unknown `client_id` | `401` `invalid_client` / `Invalid client or Invalid client credentials` (K-06); a case-variant of a real client id is the same unknown-client answer (K-03), so Keycloak client ids are case-sensitive, consistent with D-11.3's ordinal match |
| No credentials at all | `401` `invalid_client` / `Invalid client or Invalid client credentials`, no `WWW-Authenticate` header (K-07). CMS answers this before delegation (D-03 row 7), so it never reaches Keycloak |
| `sslRequired` | `external` (A-02 closed: HTTP from the Docker network and from loopback is accepted) |
| `azp` on service-account tokens | equals the client id (A-04 closed) |
| Refresh token for `client_credentials` | issued only with the client attribute `client_credentials.use_refresh_token=true` (K-00) |
| 26.7.5 behaviour on the rows CMS depends on | **identical** to 26.1.4 for every revocation response and every observed token state, K-01…K-23 included. The only version difference P1.1 found is in **introspection** (correction 2 below), which is the test-side validation path, not revocation |

**Correction 1 — wrong secret on a known confidential client is `401 unauthorized_client`, not `invalid_client` (both versions).** K-04 and K-05 observed
`{"error":"unauthorized_client","error_description":"Invalid client or Invalid client credentials"}`,
while unknown client and no credentials observed `401` `invalid_client` with the same description.
§2.1's wrong-secret row was corrected accordingly (approved). **D-10 row 4 already maps `error ∈
{invalid_client, unauthorized_client}` at 400 or 401 to `InvalidClient`, so no normalization
decision changes.** The first P1.1 commit left K-04/K-05 failing on the old prediction; the
correction commit set them to the observed value.

**Correction 2 — A-06 did not hold on 26.7.5; the observer needs the token's audience, and refresh tokens are observable only by their issuing client.** On 26.7.5 the first run reported every
freshly issued token inactive, including a client's own token introspected with its own credentials.
The server log gave the reason: `Introspection denied: client '<observer>' not in audience of token`
(event `INTROSPECT_TOKEN_ERROR`, `error="invalid_token"`). The observed facts are: 26.1.4 performs no
audience check at introspection; 26.7.5 answers `active:false` whenever the asking client is absent
from the access token's `aud`. The Red Hat build of Keycloak 26.4 migration guide describes this
check as introduced in 26.4.12 together with a deprecated compatibility switch (server option
`allow-token-introspection-without-audience-check`, or per client under *Advanced → OpenID Connect
Compatibility Modes*); upstream versions between 26.1.4 and 26.7.5 were **not** observed here, so
that threshold is the vendor's statement, not a verified one. For refresh tokens, 26.7.5's
`RefreshTokenIntrospectionProvider` admits only the client the token was issued to (the owner sees
`active:true` with `token_type_hint=refresh_token`; every other client, the observer included, gets
`active:false` with no denial reason in the log); without the hint the access-token provider runs and
its audience check fails because a refresh token's `aud` is the issuer. What the correction commit
did (approved):

1. The fixture creates a per-run client scope carrying an `oidc-audience-mapper` for the observer
   and attaches it as a default scope to the subject and public clients, so the observer is in every
   observed access token's `aud`. Creation and removal are confined to fixture-owned resources.
2. Refresh tokens are introspected by their **owning** client with `token_type_hint=refresh_token`
   (D-16 corrected). The separate observer still observes every access token, including the paired
   access token of K-14. K-14 is therefore characterized on both versions with identical results.
3. The public client's refresh token (K-18, K-00), which its owner cannot introspect (403), is
   observed through a refresh grant used as a liveness probe, performed only as the last action on
   that token because the grant may rotate it; `200` is recorded on both versions.
4. A-06 and D-16 were rewritten (§3, §7); `KEYCLOAK-SETUP.md` (P5.1) will note the audience
   requirement for operators who validate revocation through introspection; P3.2's observer setup
   will create and attach the audience scope as D-16 now describes.

**Disclosure (AC8) in the harness, after review finding 1:** every diagnostic the harness raises is
fixed text plus the operation label, the HTTP status and a body category; request and response
content never reach an exception. Every secret and token the run sees (client secrets, the user
password, Basic parameters, admin tokens, every issued access, refresh and ID token) is registered,
and the evidence log refuses a row that would contain one, so a provider that echoed a credential in
`error_description` would fail the fixture rather than reach the table. The evidence rows do carry
Keycloak's `error`, `error_description` and `WWW-Authenticate` values by design — they are the
characterization target — and the registry is what keeps those fields free of run credentials. The
offline fixtures in `KeycloakCharacterizationDiagnosticsTests.cs` plant sentinels in request
representations, credentials, tokens and echoing response bodies and assert that none appears in the
exception message, `ToString()` or inner chain, and that a leaking evidence row is refused while a
clean one is recorded.

**Other observations worth the reviewer's attention:** Keycloak's `401` responses carry no
`WWW-Authenticate` header (CMS adds its own under D-03 row 6/8, independent of Keycloak); the success
response is `200`, not `204`, with a zero-length body; the `dms-keycloak` volume mount in
`keycloak.yml` (`/var/lib/keycloak/data`) is not where the dev-mode database lives
(`/opt/keycloak/data`), so a container recreate loses the realm on both versions — the E2E scripts
always re-run `setup-keycloak.ps1`, so this only matters for manual restarts.

### 9.2 AC evidence matrix (P5.1)

Final verification, run 2026-10-02 against `112e4fb8fb5b8afd53ab4213f284659b144f2d01` (the P5.1
documentation commit) with `git status --short` empty before every run. That commit changes
documentation only: `git diff 9fbb94f12..112e4fb8f` lists five Markdown files and nothing under `src/`
or in the compose scripts, so the production code verified here is the code approved at P4.1. The
commit that records this section changes only this document.

#### 9.2.1 Environment

| Item | Value |
|---|---|
| Host | Windows 11 ARM64; .NET SDK 10.0.401; PowerShell 7.6.6; Docker Engine 29.6.1 (linux/arm64); CSharpier 1.2.5 |
| PostgreSQL (integration) | `postgres:16.8-alpine@sha256:951d0626…2d82b0` (PostgreSQL 16.8), container `dms-1327-pg-integration`, published on `127.0.0.1` and `[::1]` port 5432, trust auth |
| SQL Server (integration and MSSQL lanes) | LocalDB instance `DMS1327`, SQL Server 17.0.4025.3 Express Edition (64-bit), named pipe, Windows authentication (accepted substitution, L-10) |
| Keycloak (E2E) | `quay.io/keycloak/keycloak:26.1@sha256:044a457e04987e1fff756be3d2fa325a4ef420fa356b7034ecc9f1b693c32761`, label `version` 26.1.4 (pinned, unchanged) |
| E2E PostgreSQL | `postgres:16.8-alpine@sha256:951d0626…2d82b0` (`cs-local` stack) |
| Release build | `./build-config.ps1 Build -Configuration Release`: 0 warnings, 0 errors |
| Test assembly hashes (SHA-256) | `EdFi.DmsConfigurationService.Tests.E2E.dll` `F7C8161E73ACC3923A772B4BD450FEE572B9BD46D2ECB5B78F45C918E0270848`; `EdFi.DmsConfigurationService.Frontend.AspNetCore.dll` (host CMS of the MSSQL lanes) `CCE144CA5C4F2D734D7F06CD765AA24C02E0D5274B879CEF167E21074F828DBB`; both from the Release build above (08:45 local) |

#### 9.2.2 Runs

Commands from the repository root; `NODE_OPTIONS` cleared for every test process.

| Suite | Command | Result | Not executed |
|---|---|---|---|
| Backend unit | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Tests.Unit/…csproj -c Release --no-build` | **1895 / 1895 passed** | 0 |
| Frontend unit | `dotnet test src/config/frontend/EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit/…csproj -c Release --no-build` | **2185 / 2185 passed** | 0 |
| PostgreSQL integration | `dotnet test src/config/backend/EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration/…csproj -c Release --no-build` | **950 / 950 passed** | 62 `[Explicit]` DMS-1437 operational probes (36 step 0.2, 26 step 2.11), excluded by design |
| SQL Server integration | same for `…Backend.Mssql.Tests.Integration`, `ConnectionStrings__MssqlAdmin=Server=<LocalDB pipe>;Integrated Security=true;TrustServerCertificate=true` | **984 / 984 passed** | 66 `[Explicit]` DMS-1437 operational probes (38 step 0.2, 28 step 2.11) |
| E2E PostgreSQL × keycloak | `./build-config.ps1 E2ETest -Configuration Release -IdentityProvider keycloak -EnvironmentFile ./.env.config.e2e` (no `-SkipDockerBuild`; teardown first) | **421 passed, 0 failed** | 21: 8 multi-tenant only, 3 self-contained only, 8 characterization state checks "Not applicable" (§9.1.1), 2 undefined bindings (§9.2.4) |
| E2E PostgreSQL × self-contained | same with `-IdentityProvider self-contained` (no `-SkipDockerBuild`; teardown first) | **275 passed, 0 failed** | 167: 154 characterization (Keycloak only), 8 multi-tenant only, 3 `@KeycloakOnly` (`Revocation.feature` 08–10), 2 undefined bindings |
| E2E MSSQL representative × keycloak | LocalDB lane (below), `--filter TestCategory=MssqlRepresentative` | **27 / 27 passed** | 0 |
| E2E MSSQL representative × self-contained | LocalDB lane, same filter | **27 / 27 passed** | 0 |
| Formatting | `dotnet csharpier check src/config` | exit 1: 9 pre-existing files (§9.2.4); every file this branch touches is formatted | — |

The console summary of the two PostgreSQL lanes reports 440 tests with 19 and 165 skipped; the
TRX files report 442 results with 21 and 167 not executed. The difference is the two scenarios with
undefined bindings, which the console counts neither as passed nor as skipped.

Revocation-specific results inside those lanes: `Revocation.feature` 10/10 on PostgreSQL × keycloak,
7/7 plus the 3 `@KeycloakOnly` scenarios skipped on PostgreSQL × self-contained, and 05–07 (3/3) on each
MSSQL lane; `OwaspCriticalPaths.feature` scenario 18 passes on PostgreSQL × self-contained; the P1.1
characterization re-ran in the keycloak lane with every row passing (146 passed, the 8 recorded "Not
applicable" state checks skipped) together with its 35 offline diagnostics and observer-cleanup tests.
After the MSSQL self-contained lane, `dmscs.OpenIddictToken` held 43 tokens, 2 of them `revoked`: the
owner revocations of scenarios 05 and 06; the cross-client attempt of 07 changed nothing.

#### 9.2.3 Image identity per lane (§6.8)

| Lane | Configuration Service | Built |
|---|---|---|
| PostgreSQL × keycloak | container `ed-fi-api-config-service`, image `ed-fi-api-config-local` `sha256:897f3a90ec291ab237255afb8e27d4323f0e19ece7bbbd2315d39f89ced892f7`, `AppSettings__IdentityProvider=keycloak`, `AppSettings__Datastore=postgresql`, `IdentitySettings__Authority=http://dms-keycloak:8080/realms/edfi` | 2026-10-02T16:15:31Z, by `start-local-config.ps1 -r` inside the lane |
| PostgreSQL × self-contained | image `sha256:d20e1f0c4ccdd947e55c4db85515c9599388527dfd48f4245825d0016ea6c772`, `self-contained`, `postgresql`, `IdentitySettings__Authority=http://ed-fi-api-config:8081` | 2026-10-02T16:21:27Z, `-r` inside the lane |
| MSSQL × keycloak | host process `dotnet EdFi.DmsConfigurationService.Frontend.AspNetCore.dll` (hash above) with the container's settings, overriding `AppSettings__Datastore=mssql`, the LocalDB connection (database `edfi_configurationservice_p51_keycloak`), the claims directory, and `IdentitySettings__Authority=http://localhost:8045/realms/edfi` (the host reaches the realm on the published port, which is also the issuer) | Release build of `112e4fb8f` |
| MSSQL × self-contained | the same host process, database `edfi_configurationservice_p51_self_contained`, keys and clients seeded by the repo's `OpenIddict-Crypto.psm1` and `setup-openiddict.ps1 -InsertData` through a `docker exec` shim routed to the LocalDB pipe | Release build of `112e4fb8f` |

The MSSQL lanes stand up the standard `cs-local` stack for the provider (`start-local-config.ps1
-EnvironmentFile ./.env.config.e2e -IdentityProvider <provider> -AddE2EClaimSets`, Keycloak realm setup
included), stop the Configuration Service container, and run the Configuration Service on the host
against LocalDB, as in P3.2 (L-10). The test process gets the environment `build-config.ps1` would give
it, with `DMS_CONFIG_DATASTORE=mssql`.

#### 9.2.4 Pre-existing failures (not changed by this ticket)

- **CSharpier, 9 files**, none touched by this branch (`git diff main..HEAD` lists none of them), so
  identical to `main`:
  `Backend.Mssql/Repositories/ClaimSetRepository.cs`,
  `Backend.Postgresql/Repositories/ClaimSetRepository.cs`,
  `Backend.Tests.Unit/Model/ClaimSets/ResourceClaimActionRequestValidatorTests.cs`,
  `Backend.Tests.Unit/Models/ClaimsHierarchy/ClaimsHierarchyManagerTests.cs`,
  `Backend/Models/ClaimsHierarchy/ClaimsHierarchyManager.cs`,
  `contracts/EdFi.DmsConfigurationService.Secrets/EdFi.DmsConfigurationService.Secrets.csproj`,
  `datamodel/EdFi.DmsConfigurationService.DataModel/Model/ClaimSets/ResourceClaimActionRequests.cs`,
  `Frontend.AspNetCore.Tests.Unit/Modules/ApiClientOpenApiContractTests.cs`,
  `Frontend.AspNetCore.Tests.Unit/Modules/ClaimSetModuleTests.cs`
  (paths under `src/config/`, project prefix `EdFi.DmsConfigurationService.` omitted).
- **Undefined bindings, 2 scenarios** in `ApiClients.feature`, on both PostgreSQL lanes:
  `_09VerifyUpdatedApiClientHasCorrectValues` (`And a PUT request is made to "/v3/apiClients/{apiClientId}" with`)
  and `_15VerifyDeletedApiClientNoLongerExists` (`And a DELETE request is made to "/v3/apiClients/{apiClientId}"`).
  Both steps follow a `Given`, so Reqnroll looks for `[Given]` bindings, and the step definitions are
  bound only as `[When]`. The feature and both bindings are unchanged from `main`.

#### 9.2.5 AC by AC

| AC | Evidence (this run unless a step is named) |
|---|---|
| AC1 preserve DMS-1478 | Every migrated DMS-1478 fixture passes in the unit suites: `OpenIddictTokenManagerTests` (forged token, cross-client, case-only difference, non-canonical casing, expired, wrong issuer and audience, failure-category logging) and `IdentityModuleTests` ownership fixtures; the engine lookup tests in both integration suites are unchanged and pass; `/connect/token` fixtures unchanged; `OwaspCriticalPaths.feature` scenario 18 (revoked token rejected on reuse) passes on self-contained |
| AC2 authentication | `IdentityModuleTests`: every D-03 row and worked example, every D-04 decoding case, duplicates of the four parameters and of `Authorization`, the precedence pairs, manager not called on every shape or credential failure. `KeycloakTokenRevocationManagerTests`: every D-11.3 row asserts whether a revoke request was sent; only a proven confidential client gets one. E2E on both providers and both engines: `Revocation.feature` 01–04 (shape, mixed, no credentials, wrong Basic secret with challenge) on PostgreSQL, owner success with Basic (05) and form (06) on all four lanes; Keycloak public client with no secret, an arbitrary form secret and an arbitrary Basic secret refused with its token still active (08–10) |
| AC3 ownership | Both integration suites: `RevokeTokenAsync(tokenId, applicationId)` revokes only the owning application's row, leaves another application's row `valid`, and a second call changes nothing and keeps `RedemptionDate` (P2.2 fixtures, re-run here). Keycloak manager tests: the form body carries the caller's credentials and never the service secret. E2E 07 (cross-client: `200`, then the token still active at the provider and still usable for a protected request) on all four lanes; the MSSQL self-contained row count above |
| AC4 response contract | `IdentityModuleTests` one fixture per §4 row and per precedence pair; `GlobalExceptionHandlerTests` and `TokenRevocationStartupTests` pipeline fixtures for D-17 (500 `server_error` in OAuth JSON, malformed multipart 400, 413/415, `/connect/token` keeps the Ed-Fi contract); E2E 01–04 and 08–10 assert the OAuth bodies and the challenge |
| AC5 shared abstraction, failure separation | `ITokenRevocationManager` in the Backend project, which gains no project reference (two `ProjectReference` entries on `main` and on the branch); `OpenIddictTokenManagerTests`: each boundary throw, each D-07.4 row, truncated stored hash, missing certificate without provisioning, mutation-then-lost-response 503 with no state assertion, comparison fault 500 not 503; Keycloak manager tests: timeouts, transport, lost response, unparseable and oversize bodies → `TemporarilyUnavailable` |
| AC6 Keycloak mapping | §9.1 evidence on 26.1.4 and 26.7.5 (P1.1); the characterization re-ran green on 26.1.4 in this run; manager tests keyed on `OwnershipMismatchDescription = "Unmatching clients"` and the observed codes, including a fixture pinning the production constant; E2E keycloak lanes: owner revoke (observer `active:false`), cross-client and public clients (observer `active:true`) |
| AC7 startup and runtime | `TokenRevocationStartupTests` in the frontend suite: all three provider/engine shapes boot; removed registration and throwing factory refuse startup with the D-12 messages, no inner exception, no exception attached to the critical log, no provider connection; a `503` leaves the host serving; a late construction failure is a `500` without the factory's text. Release note amended in P4.1 (`docs/changelog/8.1.0.md`) |
| AC8 verification and docs | Disclosure fixtures (D-15 table) in both unit suites and the characterization diagnostics; both databases (integration, MSSQL lanes) and both identity providers (all four E2E lanes); `CS-AUTH.md`, `OWASP-AUTH-COVERAGE.md`, `KEYCLOAK-SETUP.md`, the design index and the changelog updated; the token-type matrix in `CS-AUTH.md` marks offline tokens untested and Keycloak refresh-token revocation as characterized directly, not through CMS (L-08); image ids and SHAs above |

#### 9.2.6 Cleanup

After the last lane: `teardown-local-cms.ps1` removed every `cs-local` container, volume and image
(`ed-fi-api-config-local` included) and the unused `dms` network, and its own verification reported all
three removed. Both lane databases (`edfi_configurationservice_p51_keycloak`,
`edfi_configurationservice_p51_self_contained`) were dropped from LocalDB, which then held only the
integration database `edfi_configurationservice_mssql_integration`, and the instance was stopped. The
PostgreSQL integration container `dms-1327-pg-integration` is kept, as since P2.2. No lane logged a
cleanup failure; the Keycloak observer and public-client cleanup ran inside the passing keycloak lane,
and its realm was destroyed with the stack. `git status --short` was empty after every run.

### 9.3 Per-step verification record (P2.2 onward)

Each step's own commit cannot carry its SHA, so the E2E SHA and image identity required by §6.8 are
reported at the step's checkpoint and consolidated into §9.2 at P5.1.

**P2.2 environment (approved with `61e0dda8c`).** Integration tests ran on the full PostgreSQL and SQL
Server projects (950/950 and 984/984; the 62 PostgreSQL tests in the 14 `[Explicit]` DMS-1437
operational-probe fixtures are excluded by design and cover nothing in P2.2). Engines: PostgreSQL 16.8
in a temporary container (`dms-1327-pg-integration`, published on `127.0.0.1` **and** `[::1]` port 5432:
publishing only `127.0.0.1` made each connection wait about 2 s for the refused `::1` attempt and caused
timing failures); SQL Server 2025 17.0.4025.3 Express as the LocalDB instance `DMS1327`, connected
through its named pipe. The substitution is a deviation from the AGENTS.md SQL Server 2025 container,
which crashes under emulation on this ARM64 host, and was accepted for this database change by review.
Both dedicated test databases are kept for later steps.

**P2.3 decisions settled by the implementation** (none changes an approved behaviour):

| Item | Decision |
|---|---|
| Keycloak mode until P3.2 | The no-op `200` for an unregistered `ITokenRevocationManager` now runs **after** D-03 rows 1–7, so the endpoint's own shape and credential-presence checks hold in both modes (the re-purposed DMS-1218 fixture asserts `400 invalid_client` with no challenge for a credential-less Keycloak-mode request). Rows 8–12 need the manager and remain a no-op `200` there until P3.2. |
| Row 6 and row 7 descriptions | A malformed Basic value (row 6) answers "Invalid client or Invalid client credentials"; any incomplete form credentials (row 7: both missing, one missing, or one empty) answer "Client authentication is required.", independent of the client's type. |
| Space after `Basic` | `1*SP` per RFC 7235 (D-03, D-04 stage 0). Corrected after review of `4d0ac4bbf`: the first implementation split the scheme on a literal space, so `Basic<TAB>…` was not recognised as a Basic attempt and fell back to the form rules (form credentials reached the manager; without them the answer was 400 with no challenge). Recognition now uses the scheme token alone. Raw-header fixtures sent through `TestServer` (bypassing `HttpClient` normalization) pin a tab, a tab then a space, and a lower-case scheme with a tab: 401 + challenge without form credential keys, 400 `invalid_request` with any of them, the manager never called; several raw spaces still authenticate, and `Basicx …` is still a different scheme. |
| D-17 texts | 413, 415 and other `BadHttpRequestException` statuses as in the D-17 table. Caller cancellation needs no code: `ExceptionHandlerMiddleware` does not invoke the handler for an aborted request. |
| E2E observation under Keycloak | The provider-aware introspection step fails explicitly under `keycloak` until P3.2 provisions the observer client (D-16), rather than observing with credentials that were submitted to revocation. |

**P2.3 expected Keycloak gaps (`Revocation.feature`, until P3.2).** Scenarios 01–03 are decided by the
endpoint and pass under Keycloak. Scenario 04 (wrong Basic secret) answers `200` instead of `401`,
because without a registered manager nobody verifies the secret. Scenarios 05 and 06 (owner revoke)
fail at their first introspection step, and the revocation itself is still a no-op. Scenario 07 is
`@SelfContainedOnly`; its Keycloak counterpart is added with the observer in P3.2.

**P3.1 decisions settled by the implementation** (none changes an approved behaviour; the manager is not
registered, so no endpoint path reaches it until P3.2):

| Item | Decision |
|---|---|
| A-01 facade approach | The package's own `GetClientsAsync(realm, clientId, cancellationToken)`, through the new `IKeycloakClientFacade.GetClientsByClientIdAsync`. The gate reads the nullable flags directly, so a missing `publicClient` is never a model default `false`. |
| Synchronous admin token (found while resolving A-01) | Every Keycloak.Net call builds its request through `GetBaseUrl`, which fetches the admin access token with `.GetAwaiter().GetResult()` and no cancellation token, **before** the call returns a task. Passing the token alone therefore cannot bound the read, and the call would block the caller's thread. The D-11.4 wrapper is kept and extended rather than replaced: the call runs on the thread pool, the wait is `WaitAsync(timeout, TimeProvider, cancellationToken)`, and the list request also receives a linked token, cancelled on timeout and on caller cancellation. The in-flight token fetch cannot be aborted. It ends under Flurl's own default timeout (100 s) and holds a pool thread meanwhile. Each revocation makes two admin requests (token, then list). Both are recorded as limitations for P5.1. |
| Timeout source | The manager passes the named `KeycloakClient` `HttpClient`'s `Timeout` (`AppSettings:TokenRequestTimeoutSeconds`, F-24) to the facade, so every phase uses the same configured duration with no DI or configuration change. The facade method therefore takes a `TimeSpan timeout` parameter, which D-11.2's signature did not list. The phases do **not** share one elapsed-time budget: the admin read, the revoke send (until response headers) and the error-body read each get a **separate window of that duration**, started when the phase starts. A revocation can therefore wait up to three times `TokenRequestTimeoutSeconds` (up to two when the answer is 200, whose body is not read). |
| Caller cancellation | Surfaces as a fresh `OperationCanceledException` for the caller's token, with no inner exception, from both the admin read and the revoke. A cancelled Flurl call arrives as a `FlurlHttpException` whose message contains the request URL, including the caller's client id. |
| Gate details (D-11.3) | An affirmative `publicClient: true` or `bearerOnly: true` answers `InvalidClient` even if the other flag is absent. An absent `publicClient` with no affirmative refusal is `TemporarilyUnavailable` (`client-type-unknown`). A null list, a null record or a record without `clientId` anywhere in the answer is `client-type-unknown`. A 401 from the admin token request counts as a refused read, like a 401/403 from the list. An empty or blank id or secret answers `InvalidClient` before any provider contact (defence in depth behind D-03 rows 6–7). |
| Revoke request and body handling (D-09) | `HttpCompletionOption.ResponseHeadersRead`. A 200 completes on its status line and the body is not read (D-10 row 1: any body). A non-200 body is read in its own timeout window (see the timeout row), because the client's own timeout no longer covers the body. The read is capped at 64 KiB, and a declared `Content-Length` over the cap is not read at all. The body must be valid UTF-8 (`JsonDocument` accepts invalid UTF-8 inside a string and fails only when the value is read). An `error` or `error_description` string that holds an escaped, unpaired surrogate (for example `"\uD800"`) is unparseable: such a body is valid UTF-8 and valid JSON syntax, so only reading the string fails (`InvalidOperationException` from `GetString`). That read alone is guarded, so other faults still surface (corrected after review of `157900df7`, which let that exception escape as a 500). The same holds for a member **name** with such an escape, anywhere in the object and beside well-formed members, because it could be either member. Every name is decoded through `JsonProperty.Name` and compared ordinally, since `NameEquals` can answer "not equal" without decoding (corrected after review of `caa36545e`). A repeated `error` or `error_description` member is unparseable, and a non-string member is treated as absent. |
| Mapping (D-10) | Only the table's rows map, keyed on the exact §9.1 strings (`OwnershipMismatchDescription = "Unmatching clients"`, normalized only at HTTP 400). A 400/401 whose `error` matches no row is `TemporarilyUnavailable` (`unrecognized-response`), for example 401 `invalid_request` "Unmatching clients", 400 `invalid_token`, or 401 `unsupported_token_type`. Every status other than 200, 400 and 401, 204 included, is `provider-status`. |
| Lost-response classification (D-09, D-13.3) | Before headers: an `HttpRequestException` whose `HttpRequestError` is a connection, name-resolution, TLS or proxy-tunnel failure is `unreachable` (the request never reached Keycloak). Any other `HttpRequestException` (`ResponseEnded`, `InvalidResponse`, …) is `response-lost`, a refinement of D-09's single `HttpRequestException` row. A client timeout is `timeout`. While reading an error body, an I/O, HTTP or budget failure is `response-lost`. |
| `Reason` labels | Gate: `client-type-timeout`, `client-type-read-refused`, `client-type-read`, `client-type-unknown`, `client-type-ambiguous`. Revoke: `timeout`, `unreachable`, `response-lost`, `unparseable`, `unrecognized-response`, `provider-status`, `unexpected`. |
| Log levels | `TemporarilyUnavailable` at Error (status code, D-15 category, or exception type chain). Authentication and request refusals at Information. The 200 and the ownership mismatch at Debug. No log entry on this path carries an exception object. |

**P3.2 decisions settled by the implementation** (none changes an approved behaviour):

| Item | Decision |
|---|---|
| Registration (D-12) | `AddKeycloakServices` registers `ITokenRevocationManager → KeycloakTokenRevocationManager` as **transient**, alongside the scoped `KeycloakContext` and `IKeycloakClientFacade`. A unit fixture builds the container with `ValidateScopes` and resolves the manager in a scope; removing the registration or making it a singleton fails it. The endpoint still resolves the manager optionally and the startup check is not added; both are P4.1. |
| Observer (D-16) | A `[BeforeFeature]` hook bound to the feature tag `@KeycloakRevocationObserver` provisions, only when `DMS_CONFIG_IDENTITY_PROVIDER=keycloak`, a confidential client `revocation-observer-<run id>` (no service account, no grant types) and a client scope `revocation-observer-audience-<run id>` whose audience mapper adds the observer to the access token's `aud`. The scope sets `include.in.token.scope=false`, so the `scope` claim of the observed tokens is unchanged. The observer's credentials live only in the fixture object held by the `FeatureContext`; no revocation step can reach them. The matching `[AfterFeature]` hook detaches the scope from every pre-existing client it was attached to, deletes any public client and user still tracked, then the observer and the scope; every step is attempted and failures are raised together. |
| Observed clients | `Revocation.feature`'s current token belongs to the Configuration Service's own client (`DmsConfigurationService`), which the fixture did not create, so the audience scope is attached to it for the feature run and detached afterwards; the client is looked up by ordinal `clientId` match. Each public client the fixture creates gets the scope at creation. |
| Steps | `the current token is active/inactive at the identity provider` introspect through CMS `/connect/introspect` under self-contained and through the observer under keycloak (access tokens only; no scenario observes a refresh token). Scenario 04 now establishes the active-before state too. Public-client steps create a public client with direct access grants and a user through the admin API, take the access token of a password grant, and delete both in `[AfterScenario]`. |
| Scenario layout | Owner revocation (05 Basic, 06 form) and wrong-secret rejection (04) were already provider-neutral and now pass on the Keycloak lane, so no `@KeycloakOnly` duplicates were added. The cross-client scenario 07 lost `@SelfContainedOnly` and runs on both lanes: its 200 and the introspection that follows hold for both providers, and its `GET /v3/vendors` is the self-contained protected-resource proof (true but not probative under Keycloak, which validates JWTs locally). `OwaspCriticalPaths.feature` scenario 18 keeps `@SelfContainedOnly` (D-16). New `@KeycloakOnly` scenarios 08–10: a public client with no secret (400 `invalid_client` "Client authentication is required.", D-03 row 7), with an arbitrary form secret (400 `invalid_client`, the D-11 gate) and with an arbitrary Basic secret (401 + challenge, the gate); each asserts the public client's token active before and after. `@KeycloakOnly` is skipped when the provider is unset, unlike `@SelfContainedOnly`. |
| MSSQL representative | 05, 06 and 07 carry `@MssqlRepresentative`. The owner revocations exercise the constrained `UPDATE` end to end. The cross-client attempt (07, an addition to the plan's "owner-revoke scenarios") demonstrates end-to-end cross-client isolation, but it does **not** reach the `UPDATE`: the manager compares the token's `client_id` with the caller and answers the mismatch as completed before any mutation (D-07), so the SQL predicate on the application id is a second layer this scenario cannot observe. That predicate's protection is established by the direct repository integration tests of P2.2 on both engines. |
| Harness reuse | The observer reuses the P1.1 `KeycloakCharacterizationApi` for admin calls, token grants and introspection, extended with a client lookup by ordinal `clientId` and a default-scope detach. Its diagnostics follow the harness rule: fixed text, operation, status and body kind only. |
| Public-client cleanup (corrected after review of `0ca672bd4`) | A public client's resources are tracked as soon as Keycloak creates each of them, independently of whether the token grant succeeds. Deleting them attempts the user and the client each on its own, clears each one once deleted, and drops the entry from tracking only when nothing is left, so an undeleted resource is retried at feature teardown and a deleted one is never deleted again (Keycloak would answer 404). A failed setup rethrows its own exception; its cleanup failures are written to the test output by exception type name only. A failed scenario cleanup raises its failures together and leaves the remainder for teardown. Offline fixtures against a scripted Keycloak pin each rule (`KeycloakRevocationObserverCleanupTests.cs`). |

**P4.1 decisions settled by the implementation** (none changes an approved behaviour):

| Item | Decision |
|---|---|
| Placement (D-12) | `EnsureTokenRevocationSupport` runs after the plugin audit **and after** the `IOptions<DatabaseOptions>` resolution, not immediately after the audit, and still before `ReportInvalidConfiguration` and the database work. The self-contained manager's repository reads `DatabaseOptions.Value` in its constructor, so a check placed before that resolution would report a rejected encryption key as a manager that "could not be constructed (`Microsoft.Extensions.Options.OptionsValidationException`)", without the validator's text. `DatabaseOptionsStartupTests` fails with the check moved there. Neither manager reads `AppSettings` or `IdentitySettings` options when constructed, so an invalid section still reaches the reporting middleware. |
| `{Provider}` value | D-12 relies on `AppSettingsValidator`, but that validator runs later, in `ReportInvalidConfiguration`. The restriction that holds at the check is `ConfigureIdentityProvider` in `AddServices`, which throws for any value other than `keycloak` or `self-contained` (case-insensitive). The check logs a fixed literal mapped from the setting (`keycloak`, `self-contained`, otherwise `unrecognized`), never the configured text. |
| Scope and disposal | The scope is created and released inside the `try`, so an exception from disposing what the registration constructed is reported the same way (type names only, "could not be constructed") and never escapes with its message. The shipped self-contained manager is a singleton: resolving it in the scope constructs it in the root container, where it is kept, rather than being discarded with the scope. |
| Endpoint | `RevokeToken` takes `[FromServices] ITokenRevocationManager`; the optional resolution and its no-op `200` are removed. The manager is therefore constructed when the request is bound, before the D-03 shape checks run; construction does no I/O and the shape checks still decide before the manager is **called**. A construction failure at request time (one the startup check did not see) is a 500 `server_error` through D-17. Two endpoint fixtures depended on the removed path: the DMS-1478 unauthenticated-caller fixture now registers a fake manager and also asserts it is not consulted, and the P2.3 "no registered manager answers 200" fixture is removed, because that state can no longer start. |
| Startup tests | `TokenRevocationStartupTests` boots self-contained/postgresql, self-contained/mssql and keycloak/postgresql with `ValidateScopes` on (as in Development, so a root resolution of the Keycloak manager fails), each with `IdentitySettings:Authority` pointing at a loopback listener that counts connections and resets them unanswered. Per shape: the shipped registration starts with no critical log and no connection; removing it, or a factory that throws an exception carrying a sentinel in its message, inner exception and `Data`, fails startup with the exact D-12 message (the type chain for the latter), an `InvalidOperationException` with no inner exception, one critical log with no exception attached, the sentinel absent from every captured log field, scope and exception chain, and no connection. A fake manager answering `TemporarilyUnavailable` then `Completed` gives 503 then 200 with the host not stopping. With the real Keycloak manager, the first revocation reaches the listener (so the startup count of zero is an observation, not a blind spot) and is answered 503; the next request is served (400 for a missing token) and a retry reaches the listener again. A registration that constructs at startup and throws on the next resolution answers that request 500 `server_error` without the factory's text, and the following request is served. Mutations (check removed, check before `DatabaseOptions`, exception logged, exception wrapped, root resolution, missing registration accepted) each fail these tests or `DatabaseOptionsStartupTests`. |
| Request-time logging (corrected after review of `dd021d94a`) | The late-construction fixture showed the response was safe but the log was not: `RequestLoggingMiddleware` attached the factory's exception to `HttpRequestFailed`. The route now carries `ExceptionTypeOnlyLoggingMetadata` and its failures are logged by exception type names only (D-15 rule 3, D-17 logging boundary). The fixture's exception carries sentinels in its message, inner exception and `Data`; the fixture asserts their absence from every captured log field, scope, attached exception chain and the response, one `HttpRequestFailed` event with `ExceptionTypes` and no exception object, and the request's `TraceId` on both the event and its scope. Mutations (marker removed from the route, exception attached on the handled path, original rethrown, cancellation rethrown as is) each fail these tests or the `RequestLoggingMiddleware` fixtures. The shared type-chain helper moved to `ExceptionTypeNames` (frontend `Infrastructure`), used by the startup check and the middleware. |
| Exception content boundary (corrected after review of `f4da39efe`) | The type-only event of `f4da39efe` ran in `RequestLoggingMiddleware`, outside `UseExceptionHandler`, so the framework's exception middleware had already logged the original when the response had started or the error handler failed. `ExceptionContentBoundaryMiddleware` now withholds the content immediately after routing, inside that middleware, replacing each exception by one of the same category (D-17 logging boundary table) and keeping the original type chain on `WithheldExceptionFeature` for the failed-request event. Real-pipeline regressions capture every logger category at Trace through a provider-specific filter and fail binding on `/connect/revoke` after starting the response, and before it with an outermost test middleware making every response write fail: on both, a `Microsoft.AspNetCore` record carries an attached exception, and no record, field, scope, attached exception chain or `Data` carries the sentinel or an original exception type; the failed-request event names the original types. The same two failures on `/connect/token` (unmarked) still log the original exception. Mutations (boundary removed, boundary before routing, malformed-form, status-code and cancellation categories lost, feature not set, original kept as inner) each fail these tests, the boundary unit fixtures or the existing malformed-form pipeline fixture. TestServer does not run Kestrel, so the server's own log of an escaping exception is covered by the `RequestLoggingMiddleware` unit fixtures (the replacement it rethrows), not end to end. |
| Aborted-request `IOException` (corrected after review of `19612c773`) | The first replacement branch matched only `OperationCanceledException`, so an `IOException` raised while the caller had aborted became an `InvalidOperationException` and took the 500 error-handler path, which the framework reserves for real failures. The branch now matches `OperationCanceledException or IOException` while `RequestAborted` is cancelled. Real-pipeline fixtures wrap `GlobalExceptionHandler` in a recording handler and fail binding with a sentinel-bearing `IOException`: after `HttpContext.Abort()`, the framework logs "The request was aborted by the client.", the handler is never invoked, the request is logged as completed with 499 and not as failed, and no logger category carries the sentinel; without the abort, the handler is invoked once, the answer is 500 `server_error`, and the one failed-request event names `System.IO.IOException -> System.TimeoutException` with no exception attached. Unit fixtures cover `OperationCanceledException`, `IOException` and `BadHttpRequestException` on an aborted request, and `IOException` on a live one. Reverting the branch fails them. |

### 9.4 Known limitations (P5.1)

Recorded limitations of the shipped behaviour, each with the decision that accepted it. The
operator-facing subset is published in `CS-AUTH.md` (§ Known limitations, § Plugin compatibility
requirement).

| # | Limitation | Source |
|---|---|---|
| L-01 | **Replacement-hasher compatibility.** A replacement `IClientSecretHasher` is called through `VerifySecretAsync`; only a plugin that throws for operational failures keeps them apart from `invalid_client`. A suppressing plugin is non-conforming, and the contract is not changed to enforce it. | Q-09 |
| L-02 | **Shared-validator failure suppression.** `JwtTokenValidator.ValidateToken` reports every verification failure as a token outcome, so a key that imports but cannot be used at request time makes revocation answer `200` without revoking. Not bounded by this design. | D-07.6 |
| L-03 | **Synchronous admin-token fetching.** Keycloak.Net fetches the admin token with `.GetAwaiter().GetResult()` and no cancellation inside `GetBaseUrl`. The bounded wait stops waiting, but timing out the wait does not stop an in-flight fetch: it cannot be aborted, holds a pool thread until Flurl's 100 s default timeout and its result is discarded. | §9.3 P3.1, D-11.4 |
| L-04 | **Two admin requests per revocation** (token, then client list), before the revoke itself and before Keycloak authenticates the caller: any request supplying a client id and secret triggers them, valid or not. A request without credentials is refused locally; an unknown client id never reaches the revoke. | Q-01, §9.3 P3.1 |
| L-05 | **Separate timeout windows.** Admin read, revoke send (until headers) and error-body read each get their own `TokenRequestTimeoutSeconds` window: up to 3× the setting (2× when Keycloak answers `200`). | §9.3 P3.1 |
| L-06 | **Basic decoding differs from `/connect/token`.** `/connect/revoke` decodes strictly per RFC 6749 §2.3.1 (`+` is a space, complete `%` escapes, strict UTF-8 and base64, `1*SP` separator, no fallback to form fields); `/connect/token` keeps `Uri.UnescapeDataString` and its form fallback. | D-04 |
| L-07 | **Propagation.** CMS and DMS validate Keycloak tokens locally (CMS default 5 min skew, DMS `ClockSkewSeconds` default 30 s) and DMS self-inspects self-contained tokens, so a revoked token is accepted there until `exp` plus skew. No per-request introspection (non-goal). | D-16 |
| L-08 | **Token types not verified end to end.** Offline tokens are untested. Keycloak refresh-token revocation is characterized directly against Keycloak (K-14), not through a CMS scenario. Bearer-only clients, a missing `azp` and an unresolvable session remain source-derived (§7). | D-16, §7 |
| L-09 | **Keycloak versions.** CMS end-to-end evidence is on the pinned 26.1.4 image only. 26.7.5 is a direct characterization of Keycloak's endpoints (§9.1.3), identical on revocation; CMS was not run against it. Not a support policy. Changing the Keycloak image requires rerunning `KeycloakRevocationCharacterizationTests` and confirming the exact ownership-mismatch answer D-10 normalizes is unchanged. | Q-02 |
| L-10 | **MSSQL evidence engine.** SQL Server integration tests and the MSSQL representative E2E lanes run on LocalDB 17.0 (SQL Server 2025 Express), with the Configuration Service as a host process for the E2E lanes, because the SQL Server container crashes under emulation on the ARM64 host. Accepted for this ticket, which is not about the engine. | §9.3 P2.2, §9.2 |
| L-11 | **Outside the exception content boundary.** An exception raised before routing (tenant resolution, reverse-proxy and security-header middleware) and transport failures before the endpoint is selected keep their framework handling. The stack trace is given up on the revoke route. TestServer does not run Kestrel, so the server's own log of an escaping replacement is covered by unit fixtures, not end to end. | D-17, §9.3 P4.1 |
| L-12 | **Pre-change token casing.** Self-contained tokens minted before DMS-1478 with a non-canonical `client_id` casing may resist revocation by a canonically cased caller until they expire. | DMS-1478, `CS-AUTH.md` |
| L-13 | **Stale ADR reference.** `reference/adr-client-id-casing.md` still names `ITokenRevocationManager.AuthenticateClientAsync`, removed by D-02. The ADR is a decision record and is unchanged by this ticket (§5 AC1); the decision it records is kept inside `RevokeTokenAsync`. | D-02, §5 |
| L-14 | **Unauthenticated admin load (deferred).** Because of L-03 and L-04, requests carrying unverified credentials can cause admin traffic and, with a slow Keycloak, hold pool threads. Bounding it (admin-token caching, rate limiting `/connect/revoke`) is deferred availability work, not part of this ticket; AC2 permits authentication in the delegated request and requires the supplementary check to fail closed, which it does. Token caching alone would also have to handle concurrent cache misses and refresh failures. | Review `dms-1327-01` finding 2 |
