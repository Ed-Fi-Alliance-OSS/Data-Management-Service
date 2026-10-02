# DMS-1579 Token-Limit Exemption Design

Status: approved in design discussion on 2026-10-02

Repository baseline: branch `DMS-1579`, commit `5c964676`

## 1. Intent and authority

DMS 8.1 will ship `IdentitySettings:BearerTokenPerClientLimit` with a default of `15`, continue
enforcing the limit for Ed-Fi API clients, and exempt the non-API OpenIddict clients that DMS and
CMS use for system and administrative work. This prevents DMS replicas and restarts from exhausting
their shared Configuration Service client's token allowance and failing startup.

The authoritative requirement is `.plans/ref/DMS-1579.md`. The human and nano pre-specs and the
reconciliation report are supporting evidence only:

- `.plans/DMS-1579-implementation-pre-spec.md`
- `.plans/DMS-1579-implementation-pre-spec.nano.md`
- `.plans/ref/DMS-1579-pre-spec-review.md`

The reconciliation report's corrections are incorporated here, especially its fail-closed model,
manager/repository integration coverage, environment-file requirement, and PRD conflict. The
story-linked ODS value could not be independently fetched; this design claims only that `15` is the
story-required value, not broader behavioral parity with ODS. Jira-only sibling information was
also unavailable. Local DMS-1354, DMS-1430, and DMS-1459 history and the linked repository design
records were inspected.

## 2. Approved decisions

1. **Exemption boundary:** an OpenIddict application is limited when it has at least one live
   `dmscs.ApiClient` row and exempt when it has none. Scopes are not classifiers. This intentionally
   exempts DMS's Configuration Service credential, CMS admin/bootstrap clients, and other supported
   non-API clients—not only one configured client ID.
2. **PRD alignment:** amend `docs/PRD-v8.1.md` FR-AUTHN-7 from “a single client” to “a single Ed-Fi
   API client.”
3. **Technical approach:** derive a fail-closed exemption fact as part of application lookup, let
   `OpenIddictTokenManager` select the effective limit, and reuse the existing `maxActiveTokens < 1`
   repository path unchanged.

This approach avoids an exempt-client allowlist, a new scope or marker contract, an additional
classification query, policy duplicated inside dialect-specific token insertion SQL, and any new
schema state.

## 3. Scope

### Included

- Raise the code, binding, application, Compose, and tracked environment defaults to `15`.
- Add a fail-closed token-limit exemption fact to `ApplicationInfo` and populate it in PostgreSQL
  and SQL Server application lookups.
- Select an exempt or configured effective limit in `OpenIddictTokenManager`.
- Add unit and per-dialect integration coverage that proves the classifier and manager/store
  composition.
- Preserve the DMS `/oauth/token` 429 contract with existing regression coverage.
- Make the Instance Management route-context E2E fixture reuse valid tokens so its shared API
  credentials remain below the now-enabled limit.
- Update the configuration guidance, Docker guidance, token-cleanup design record, 8.1 PRD, and
  8.1.0 changelog.

### Excluded

- DMS runtime token caching or startup-flow changes.
- Keycloak behavior or configuration.
- Schema migrations, generated DDL changes, DMS relational mappings, or a
  `SchemaHashConstants.RelationalMappingVersion` bump.
- Changes to token cleanup cadence, token lifetime, token revocation, or storage locking.
- Changes to the CMS problem response or DMS OAuth response parser/rebuilder.
- A multi-replica test harness; the story explicitly permits simulated grants.
- Unrelated cleanup or a generalized client-classification subsystem.

## 4. Chosen design

### 4.1 Classification contract

Add `ApplicationInfo.IsTokenLimitExempt` as a non-nullable `bool` with CLR default `false`.
Defaulting to false is a security invariant: an object created by a unit test, an incomplete future
mapping, or a lookup that does not deliberately populate the fact remains limited.

Both repository implementations return an accurate value for application lookups by client ID and
by application ID, even though token issuance currently consumes only the client-ID lookup. This
keeps `ApplicationInfo` semantics independent of which repository method produced it.

- PostgreSQL derives `IsTokenLimitExempt` from the existing `LEFT JOIN` to `dmscs.ApiClient`: zero
  non-null matching `ApiClient` IDs means exempt. Existing scope, data-store, and approval
  aggregation remains unchanged.
- SQL Server derives both approval and exemption in its existing API-client lookup, using the
  canonical stored `ApplicationRow.ClientId`. It must not classify using the request-supplied client
  ID because that would reintroduce casing ambiguity addressed by the client-ID casing contract.

`IsApproved` is not reused for classification. It represents approval state and intentionally
defaults to true when no API-client row exists; combining the two concerns would obscure both
contracts.

### 4.2 Token-grant data flow

The request follows the existing validation sequence:

1. `OpenIddictTokenManager.GetAccessTokenAsync` loads `ApplicationInfo` by client ID.
2. It validates the secret and the existing approval rule.
3. It mints the JWT using the canonical stored client ID.
4. Immediately before calling `StoreTokenAsync`, it selects:
   - `-1` when `IsTokenLimitExempt` is true;
   - the configured `BearerTokenPerClientLimit` otherwise, including configured values below 1.
5. The unchanged store handles the result:
   - below 1 uses the existing unconditional insertion path;
   - a positive value uses the existing locked count-and-conditional-insert path.

The manager's handling of `Stored`, `LimitExceeded`, `ClientNotFound`, and `LockTimeout` does not
change. An exempt application cannot ordinarily return `LimitExceeded`; a limited application
continues to report the configured positive value in `FailureTokenLimitExceeded`.

### 4.3 Defaults and configuration surfaces

Change `5` to `15` in:

- `IdentityOptions.BearerTokenPerClientLimit`
- `OpenIddictServiceCollectionExtensions` binding fallback
- CMS `appsettings.json`
- CMS `appsettings.Development.json.example`
- `eng/docker-compose/local-config.yml`
- `eng/docker-compose/published-config.yml`
- all twelve tracked `eng/docker-compose/.env*` files that set
  `DMS_CONFIG_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT`

The twelve environment files are `.env.config.e2e`, `.env.config.mssql.e2e`,
`.env.config.mssql.multitenant.e2e`, `.env.cursorpartitions.e2e`, `.env.e2e`, `.env.example`,
`.env.multitenancy`, `.env.routeContext.e2e`, `.env.smoke`, `.env.smoke.ds61`, `.env.template`, and
`.env.template.ds61`.

Comments must explain that the positive limit applies to Ed-Fi API clients backed by `ApiClient`;
non-API system/admin clients are exempt, and values below 1 still disable enforcement globally.
Explicit operator values continue to override the default.

### 4.4 Route-context E2E compatibility

The route-context suite uses run-scoped fixture API credentials across many scenarios. Its current
authentication steps always mint new tokens, so enabling limit 15 would eventually reject those
shared clients.

Add an expiry-aware reusable-token operation to
`src/dms/tests/EdFi.InstanceManagement.Tests.E2E/Management/TokenHelper.cs`:

- cache by token URL and client key without logging keys, secrets, or tokens;
- use `TokenResponse.ExpiresIn` to avoid returning a token near or past expiration;
- coalesce concurrent refreshes for the same key so parallel scenarios do not mint duplicate
  replacement tokens;
- retain the current uncached method for identity lifecycle tests that intentionally need a newly
  issued token.

The generic route/tenant authentication steps and the wrong-role management helper use the reusable
operation. Scenario-specific credentials naturally get their own cache entries; identity tests that
exercise claim changes, reassignment, deletion, or distinct-token behavior continue using the
uncached path.

### 4.5 Documentation and release notes

- `docs/CONFIGURATION.md`: document default 15, the `ApiClient`-backed enforcement boundary, global
  below-1 disable semantics, and the existing 429 behavior. Remove all guidance that sizes the
  value by DMS replica/restart counts or recommends an unshared DMS client ID as the remedy.
- `docs/DOCKER.md`: make the same default and exemption corrections and remove replica-sizing text.
- `reference/design/configuration-service/TOKEN-CLEANUP.md`: qualify rate-limited growth and the
  active-token ceiling as applying to limited Ed-Fi API clients. Explain that exempt clients are
  bounded by expiry cleanup rather than an active-token ceiling.
- `docs/PRD-v8.1.md`: change FR-AUTHN-7 to “a single Ed-Fi API client.” This is an approved scope
  addition needed to keep the maintained PRD consistent with the story.
- `docs/changelog/8.1.0.md`: add a breaking-change entry describing ship-state behavior—15 active
  tokens per Ed-Fi API client, non-API clients exempt, below 1 disables enforcement, and the
  canonical 429 retained. Follow the existing bold-lead, no-inline-ticket-ID style.
- `reference/adr-oauth-upstream-error-disclosure.md`: no change. It specifies response
  transformation and contains no assumption about which clients are counted.
- `eng/azure-vm/compose/*`: no change. This stack explicitly uses Keycloak, so adding the
  OpenIddict-only setting would be dead configuration. This is an approved intentional no-op
  against the story's broad configuration-file wording.

## 5. Failure handling and compatibility

- **Fail closed:** only a successful repository lookup that proves no `ApiClient` row sets the
  exemption. Query failures fail the request through existing error handling; they never imply
  exemption.
- **Supported lifecycle:** API-client creation establishes both the provider credential and its
  `ApiClient` row before the credential is usable. Both supported deletion flows remove the
  provider credential before deleting the row. Therefore row absence does not create a usable,
  newly exempt credential during supported deletion. Direct manual database edits that violate
  this invariant remain unsupported.
- **Concurrency:** token-store locking and count/insert atomicity are unchanged. Classification is
  derived during the existing application lookup and adds no second round trip.
- **Global disable:** any configured value below 1 continues to disable enforcement for API and
  non-API clients.
- **Cleanup:** exempt clients can accumulate more concurrent rows, but expired rows remain subject
  to the existing cleanup sweep. No retention guarantee changes.
- **External contract:** preserve problem type
  `urn:ed-fi:api:security:authentication:too-many-tokens`, title `Too Many Tokens`, and message
  `Too many access tokens have been requested (limit is {n}). Access tokens should be reused until
  they expire.` DMS continues parsing the canonical CMS body and rebuilding its external response;
  “unchanged” does not mean byte passthrough.

## 6. Verification design

| Acceptance criterion | Design coverage | Verification |
| --- | --- | --- |
| AC1: default 15 and API client's 16th active token gets canonical 429 | Default surfaces become 15; `ApiClient`-backed applications remain limited | Update the options-binding default test. In each dialect integration suite, drive a real repository through `OpenIddictTokenManager` with default options: 15 grants succeed, the 16th returns `FailureTokenLimitExceeded(15)`, and only 15 rows exist. Retain the frontend canonical problem-details test. |
| AC2: system client exceeds configured limit 1 | No `ApiClient` row sets exemption and effective limit `-1` | In each dialect, configure limit 1 and perform at least three manager-level grants to the same real application without an `ApiClient` row. Assert three successes and three stored rows. |
| AC3: below 1 disables enforcement for every client | Limited applications receive the configured below-1 value; exempt applications use `-1` | Add manager coverage for an `ApiClient`-backed application configured with `-1`; retain both dialects' existing disabled-store-path tests. |
| AC4: PostgreSQL and SQL Server parity | Both dialects implement the same derived fact and unchanged storage contract | Each dialect directly verifies row/no-row classification and executes the composed AC1 and AC2 scenarios with its real repository. Manager signing/secret collaborators may use deterministic test helpers; application lookup and token storage must be real. |
| AC5: DMS `/oauth/token` 429 unchanged | No DMS production or response-contract change | Preserve the existing `OAuthManagerTests` canonical-429 parsing and rebuilding suite as the regression guard. |
| AC6: docs and env files show 15 and explain exemption | All named surfaces are updated; route-context callers reuse tokens | Search for stale values, DMS replica-sizing guidance, and obsolete comments. During implementation validation, run the route-context E2E suite with limit 15. |
| AC7: 8.1.0 changelog entry | Release note describes behavior as it will ship | Review the entry against the acceptance criterion and current changelog style. |

Additional unit regression coverage must prove:

- an uninitialized `ApplicationInfo.IsTokenLimitExempt` remains limited;
- an explicitly exempt application passes `-1` to storage;
- a limited application passes its configured value unchanged;
- a default-options limit failure carries `15`;
- reusable E2E token acquisition reuses valid tokens, refreshes near expiry, and coalesces
  concurrent acquisition, while uncached acquisition still creates a fresh token.

A direct call to `StoreTokenAsync(..., -1)` is not exemption coverage because it passes when the
classifier or manager wiring is removed. The per-dialect composed tests are mandatory regression
proof.

## 7. Dependencies, deviations, and blockers

- Depends on the existing `OpenIddictApplication.ClientId` to `ApiClient.ClientId` lifecycle
  invariant, existing token-store below-1 semantics, and existing expired-token cleanup.
- Approved scope addition: amend PRD FR-AUTHN-7 for the Ed-Fi API-client boundary.
- Approved intentional no-ops: OAuth disclosure ADR and Keycloak-only Azure VM Compose files.
- The story-required value 15 is authoritative despite unavailable live ODS/Jira references.
- There are no remaining design blockers. Implementation planning may proceed only through
  `superpowers:writing-plans` after the written specification is reviewed and approved.
