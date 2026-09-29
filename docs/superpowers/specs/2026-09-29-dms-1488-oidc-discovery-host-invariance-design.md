# DMS-1488: Host-Invariant OIDC Discovery Design

**Status:** Approved on 2026-09-29

**Story:** `.plans/ref/DMS-1488.md`

**Supporting analysis:** `.plans/pre-spec/DMS-1488-pre-spec.md`, `.plans/pre-spec/DMS-1488-pre-spec-nano.md`, and `.plans/ref/DMS-1488-pre-spec-review.md`

## Intent

The Configuration Service's self-contained OIDC discovery document must advertise stable,
configured endpoint identities. An incoming request's scheme, host, or port must not influence the
document. This closes the CMS producer side of the Host-poisoning finding while retaining the
existing discovery contract, PathBase behavior, and tenant-independent discovery route.

The story and this approved design are authoritative. The pre-specs and reconciliation report are
evidence only.

## Scope

### Included

- Build the advertised endpoint base from `IdentitySettings:Authority` and
  `AppSettings:PathBase`.
- Apply the trusted base to the enhanced and fallback discovery branches.
- Return `Cache-Control: no-store` from the discovery endpoint, without `Vary: Host`.
- Add focused CMS frontend tests for Host invariance, exact endpoint values, cache policy,
  PathBase normalization, both discovery branches, and multi-tenant access.
- Treat the DMS-1489 consumer-side defense as a coordinated release dependency.

### Excluded

- DMS JWT-validation or metadata-retrieval changes already owned by DMS-1489.
- JWKS generation, signing-key storage, rotation, or token-claim changes.
- Keycloak or other external identity-provider discovery behavior.
- Proxy configuration or global caching/security-header middleware changes.
- New Authority or PathBase validation rules.
- Cleanup of `OpenIddictDiscoveryModule`, its diagnostic URLs, or unrelated request-derived URLs.
- A general URL-building abstraction.

## Repository Evidence and Invariants

- `OpenIdConfigurationModule.GetOpenIdConfiguration` currently builds `baseUrl` from
  `Request.Scheme`, `Request.Host`, request port, and configured PathBase. Both discovery branches
  consume it.
- `IdentitySettingsValidator` requires a nonblank Authority during valid CMS startup.
- The fallback branch already uses configured Authority as `issuer`. The enhanced provider also
  reads configured Authority; its fallback to its `baseUrl` argument is not a supported valid-startup
  case.
- `IOpenIdConnectConfigurationProvider.GetConfigurationAsync(string baseUrl, ...)` has one
  production caller. Its endpoint construction is safe when the caller supplies a trusted base.
- `Program.cs` applies configured PathBase, and `TenantResolutionMiddleware` exempts
  `/.well-known` from tenant resolution. Discovery is therefore service-wide and requires no
  `Tenant` header.
- Self-contained repository configurations model Authority as the issuer origin and PathBase as a
  separate segment. The story explicitly requires combining them.
- `origin/DMS-1489` pins discovered issuer equality, restricts `jwks_uri` to the metadata origin,
  refuses redirects, fails startup for invalid initial metadata, and fails closed during refresh.
  Adding PathBase changes only URL paths and remains compatible with its origin check.
- The relevant discovery code on the working branch matches local `main`; the pre-spec's inspection
  of `main` does not conceal a branch-specific implementation difference.

## Chosen Design

`OpenIdConfigurationModule.GetOpenIdConfiguration` computes one advertised base before selecting a
discovery branch:

```text
authorityBase = IdentitySettings.Authority with trailing '/' characters removed
pathSegment   = AppSettings.PathBase with surrounding '/' characters removed

advertisedBase = pathSegment is empty
    ? authorityBase
    : authorityBase + '/' + pathSegment
```

Composition is deliberately string-based. It preserves the configured Authority's scheme, host,
explicit port, case, and any existing path text instead of allowing `UriBuilder` to canonicalize the
configured identity. It does not semantically deduplicate an Authority path that resembles
PathBase; operators remain responsible for supplying the two settings according to their documented
roles.

The handler must not read `Request.Scheme`, `Request.Host`, request port, forwarded host values, or
other request authority data while composing discovery metadata.

The computed base flows as follows:

```text
IdentitySettings.Authority + AppSettings.PathBase
                    |
                    v
        trusted advertised endpoint base
             /                       \
            v                         v
enhanced provider argument       fallback interpolation
            \                         /
             v                       v
        existing branch-specific JSON document
                    |
                    v
      HTTP 200 with Cache-Control: no-store
```

### Enhanced branch

Pass the trusted base to the existing
`IOpenIdConnectConfigurationProvider.GetConfigurationAsync(string baseUrl, ...)` contract. Preserve
the provider interface and all existing enhanced fields, nulls, capabilities, and configured issuer
semantics. The populated endpoint fields remain token, registration, JWKS, introspection, and
revocation.

No production change to `OpenIdConnectConfigurationProvider.cs` is required: the defect is the
caller's untrusted argument, not its deterministic use of that argument.

### Fallback branch

Use the same trusted base for the existing registration, token, revocation, and JWKS URLs. Preserve
the fallback branch's current field set and values; it does not gain the enhanced branch's fields.

### Cache policy

Set `Cache-Control: no-store` on successful discovery responses from both branches. Do not emit
`Vary: Host`: the representation is Host-invariant, and preventing storage directly removes the
poisoned shared-cache persistence mechanism without creating Host-keyed variants.

## Affected Contracts and Components

| Component | Change |
| --- | --- |
| `OpenIdConfigurationModule.GetOpenIdConfiguration` | Replace request-derived base construction with configured Authority/PathBase composition; set `no-store`; feed both branches the trusted base. |
| `IOpenIdConnectConfigurationProvider` | No signature or semantic change. It receives a trusted configured base from its only production caller. |
| Discovery JSON | Preserve field names, values other than affected endpoint URLs, branch-specific fields, null behavior, and serialization ordering. |
| Tenant middleware and routing | No code change. Existing `/.well-known` exemption and PathBase routing are regression-tested. |
| CMS frontend unit tests | Add a focused discovery fixture using the established `WebApplicationFactory<Program>` pattern. |
| DMS-1489 | External delivery dependency; no DMS-1489 code is copied into this change. |

## Compatibility and Failure Handling

- Deployments that relied on an arbitrary request Host to select advertised public endpoints will
  instead receive the configured Authority plus PathBase. This is the intended security correction;
  Authority must describe the issuer identity expected by clients and DMS.
- Configured Authority remains the exact issuer. Trimming trailing slashes for endpoint composition
  must not alter the issuer value returned by either branch.
- Empty PathBase produces Authority-root endpoints. A slash-surrounded PathBase is represented once
  with one separator.
- Keycloak behavior is unchanged because `OpenIdConfigurationModule` is not mapped when CMS uses an
  external identity provider.
- Multi-tenant discovery remains shared and reachable without a `Tenant` header.
- Existing Authority startup validation and provider exception handling remain in force. This story
  does not add URI parsing, invalid-configuration behavior, retries, or fallback error handling.
- No request-derived value is newly logged.
- DMS-1489 remains compatible when discovery metadata and JWKS paths differ, because it compares
  scheme, host, and port rather than path.

## Verification Design

Use NUnit, FluentAssertions, the repository's `Given_...` fixture and `It_...` test naming, and the
existing CMS `WebApplicationFactory<Program>` approach. Tests should read raw response bytes when
asserting document identity and parse JSON only for exact semantic assertions.

The registered production provider exercises the enhanced branch. Removing
`IOpenIdConnectConfigurationProvider` from the test service collection exercises the real fallback
branch; no new production switch is introduced.

Required coverage:

1. Enhanced discovery with a configured Authority and PathBase returns the exact configured token,
   registration, JWKS, introspection, and revocation URLs.
2. Fallback discovery returns the exact configured token, registration, JWKS, and revocation URLs
   while preserving its existing field set.
3. For each branch, otherwise identical requests with different Host headers return byte-identical
   response bodies.
4. A request authority whose scheme, host, and explicit port differ from configured Authority does
   not affect any endpoint value.
5. Authority with a trailing slash and PathBase with surrounding slashes produce a single separator
   and one PathBase segment.
6. Empty PathBase produces endpoint URLs directly under Authority.
7. Both branches return a cache-control header whose `NoStore` property is true and do not add a
   Host vary rule.
8. With multi-tenancy enabled and PathBase configured, discovery succeeds without a `Tenant` header
   and advertises the configured base.

No local tests are part of the brainstorming/specification activity. Test execution belongs to the
later implementation plan.

## Acceptance-Criteria Traceability

| Acceptance criterion | Design | Verification |
| --- | --- | --- |
| Arbitrary Host returns endpoint URLs derived from configured Authority and unchanged by Host | One configured advertised base; no request authority input; both branches consume it | Exact endpoint assertions in enhanced and fallback cases, including a hostile request authority |
| Response carries explicit cache directives | Successful discovery responses emit `Cache-Control: no-store` | Typed cache-control assertions for both branches; assert no Host variation |
| Documents are byte-identical across requests differing only in Host | No request-derived value enters either document | Raw response-body comparison within each branch |
| PathBase deployments remain correct | Normalize surrounding PathBase slashes and append the nonempty segment once | Empty and slash-surrounded PathBase cases with exact URLs |
| Multi-tenant deployments remain correct | Preserve tenant-independent `/.well-known` routing and service-wide configuration | Multi-tenancy enabled, no `Tenant` header, configured PathBase, successful exact response |
| Fix together with finding 2 | DMS-1489 is a release dependency; its consumer protections are not duplicated | Record the coordinated delivery gate and retain DMS-1489's issuer/origin/redirect/startup/refresh coverage |

## Dependencies, Deviations, and Blockers

- **Dependency:** DMS-1489 must ship with this producer correction for the combined finding to be
  considered remediated. Development and review may occur independently; release must be
  coordinated.
- **Approved deviations:** None. The design implements the story's configured-Authority/PathBase and
  explicit-cache requirements directly.
- **Remaining implementation blockers:** None.
- **Evidence gaps:** DMS-1483, DMS-1484, and the parent review are not accessible from this checkout.
  The supplied GitHub report URL is the repository landing page, not a review report. These gaps do
  not block the approved design because the authoritative story explicitly permits `no-store`, and
  the local reconciliation report plus `origin/DMS-1489` establish the companion boundary. If later
  authoritative material conflicts with either decision, return the design for approval before
  implementation.

## Reconciliation Check

- The human and nano pre-specs describe the same analysis; no unresolved discrepancy exists between
  them.
- The reconciliation report correctly rejects cross-branch field equality. This design requires
  Host invariance within each branch while preserving their intentional field differences.
- Provider signature removal remains optional in the pre-spec analysis and is explicitly rejected
  here as unnecessary complexity.
- `Cache-Control: no-store` is promoted from a pre-spec proposal to an approved design decision.
- The design adds no unsupported proxy, Keycloak, JWT-validation, or general caching requirement.
