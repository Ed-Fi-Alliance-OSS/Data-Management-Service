# DMS-1556 investigation record

Evidence log for the Phase 0 investigation defined in
[DMS-1556-cms-signing-key-resolution-under-load.md](DMS-1556-cms-signing-key-resolution-under-load.md) (§3).
Each step lands as an increment of this document. Raw captures that exceed what belongs in
the repository go to Jira DMS-1556.

**Scope reminder:** nothing in this document establishes the ticket's failure mechanism
until the E1+ experiments run. Step 0.2 in particular establishes *integration
feasibility* of the proposed design mechanisms; it does **not** establish the root cause
and satisfies no part of AC 1.

## 0.2 — E0 framework probes (2026-09-30)

### What E0 answers

The spec's design (D-2 plain `IConfigurationManager<OpenIdConnectConfiguration>`, D-3
request-boundary classification via `context.Fail(exception)`, I-5 no metadata/backchannel
fallback) rests on verified-by-reading framework facts V-1, V-2, and V-5. E0 pins those
facts *empirically* on the exact versions CMS ships with, so later phases cannot be
undermined by a misreading. Framework observations are distinguished from behavior the
scratch provider itself supplies; the scratch provider is a stand-in, not the design.

### Environment and reproducibility

- Probe project: `eng/performance/dms-1556/E0Probes` (scratch; not in any solution or CI
  lane). Command: `dotnet test eng/performance/dms-1556/E0Probes` (SDK 10.0.401).
- Pinned versions, matching `src/Directory.Packages.props`:
  `Microsoft.AspNetCore.Authentication.JwtBearer` / `Microsoft.AspNetCore.TestHost`
  **10.0.1**, `Microsoft.IdentityModel.*` **8.12.0**. The project opts out of central
  package management so a nested worktree cannot silently float these.
- Host shape: in-process TestServer whose Bearer scheme mirrors the CMS self-contained
  configuration (`WebApplicationBuilderExtensions.cs:335-384`: `Authority`,
  `MetadataAddress`, `SaveToken`, `Audience`, `RequireHttpsMetadata=false`, TVP with
  `ValidateAudience/Issuer/IssuerSigningKey`, `ValidIssuer=Authority`, `RoleClaimType`),
  with two probe differences: `options.ConfigurationManager` supplies a plain scratch
  manager instead of the blocking `IssuerSigningKeyResolver`, and probe events capture
  pipeline observations. Tokens are minted with a probe RSA key whose public half the
  manager publishes — the same shape as CMS database-key mode.

### Results (24/24 passed, `Failed: 0`, first run; re-run green after formatting)

| Probe | Fixture | Framework fact pinned (F) / scratch-provider behavior (S) | Observed |
| --- | --- | --- | --- |
| E0(a)1 | `Given_a_throwing_manager_without_failure_translation` | **F (V-1):** manager exception type is delivered to `OnAuthenticationFailed`; with no event supplying a result the handler **rethrows** (today's 500 shape). | `ProbeDependencyException` captured in the event and observed by the client as the thrown exception; manager called exactly once. |
| E0(a)2 | `Given_a_throwing_manager_with_failure_translation` | **F (V-1):** `context.Fail(exception)` in `OnAuthenticationFailed` prevents the rethrow; `OnChallenge.AuthenticateFailure` is the exact exception. | 401 challenge instead of an escaped exception; `AuthenticateFailure` is `ProbeDependencyException`. |
| E0(b) | `Given_failure_at_the_message_received_boundary` | **F:** `context.Fail(exception)` in `OnMessageReceived` short-circuits the handler — **no `GetConfigurationAsync` call follows** — and the exception reaches `OnChallenge`. | Manager call count **0**; 401 challenge; `AuthenticateFailure` is the boundary's exception. This is the load-shedding property D-3 relies on during retry-delay windows. |
| E0(c) | `Given_a_supplied_manager_with_authority_and_metadata_address_set` | **F (V-5/I-5):** with a `ConfigurationManager` supplied, `Authority` + `MetadataAddress` set exactly as CMS sets them cause **zero backchannel HTTP traffic**, and post-configuration keeps the supplied manager. | Authority pointed at a live local TCP socket counting connection attempts: 3/3 authenticated requests returned 200 and the counter stayed **0**; `IOptionsMonitor` shows the same manager instance after post-configuration. |
| E0(d)1 | `Given_a_completed_request_with_a_recording_manager` | **F (V-2):** the token passed to `GetConfigurationAsync` **is** the request's `HttpContext.RequestAborted` (token equality, not just linkage). | Recorded token equals the middleware-captured `RequestAborted` for the same request. |
| E0(d)2 | `Given_cancellation_during_a_gated_cold_load` | **F:** aborting a request cancels the token *inside* `GetConfigurationAsync`. **S:** waiter-only detachment — the canceled request leaves the shared load while the other waiter completes — is the scratch provider's single-flight design (the D-5 shape), demonstrated on top of the framework fact. | With two requests gated inside one cold load: canceling request A produced `OperationCanceledException` at A's waiter and at A's client; the surviving waiter then completed and request B returned **200**. |
| E0(e) | `Given_the_boundary_translation_prototype` | **S:** cold/expired/usable are scratch-provider states. **F:** the `Fail` → challenge mechanics (pinned in a/b) and, on the recovered 200, V-2's clone facts: the `TokenValidationParameters` actually handed to token validation has `ConfigurationManager == null` and the manager's signing keys are concatenated into `IssuerSigningKeys` (observed by a recording `JsonWebTokenHandler` registered in `options.TokenHandlers`). | Cold → **503 + Retry-After**; expired → **503**; recovered → **200**; manager called only for the recovered request (boundary made no retrieval while unavailable); captured clone: `ConfigurationManager` null, `IssuerSigningKeys` contains the probe kid. |
| E0(f) | `Given_audience_configuration_from_options` | **F (V-5):** `options.Audience` becomes `ValidAudience` when the TVP sets none — exactly the CMS self-contained shape (`ValidateAudience=true`, no `ValidAudience`). | Token with the configured audience → 200; different audience → 401 with `SecurityTokenInvalidAudienceException` captured in `OnAuthenticationFailed`. |

### Contradictions

None. Every probed behavior matched the spec's §1.3 readings (V-1, V-2, V-5) on the
pinned versions. Two nuances worth recording:

- E0(a)1 confirms that without event-supplied results the manager's exception escapes the
  pipeline — the design's 503 mapping (D-3/D-9) is therefore *required*, not cosmetic, to
  avoid replacing today's 500s with different 500s.
- E0(d)1's token **equality** (not merely linked cancellation) means the boundary and the
  provider may key per-request behavior off the same token identity the handler uses.

### What E0 deliberately does not show

No load, no database, no thread-pool interaction, no reproduction of the reported 500s:
those are steps 0.3–0.5 (E1–E5/E7) and gates G1/G2. AC 1 remains evidence-gated.
