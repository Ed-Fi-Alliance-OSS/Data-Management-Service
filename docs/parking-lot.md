# Parking Lot

Adjacent issues found while implementing `tasks/plan.md` (authenticate and authorize
`POST /connect/revoke`) that are **out of scope for that plan and deliberately not fixed here**.

> **Reviewer:** please read this file as part of reviewing this change, and **delete it before
> merging to `main`** — it is a working note, not shipping documentation.

---

## 1. Minted tokens carry the request-supplied `client_id` casing, not the stored casing

**Where**

- `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/OpenIddictTokenManager.cs:186-190`
  — `GetAccessTokenAsync` reads `clientId` straight from the request credentials.
- `src/config/backend/EdFi.DmsConfigurationService.Backend.OpenIddict/Services/OpenIddictTokenManager.cs:285`
  — that request-supplied value is passed to `GenerateJwtTokenAsync(applicationInfo, clientId, listOfScopes)`
  and so into the `client_id` claim, even though `applicationInfo.ClientId`
  (`Models/ApplicationInfo.cs:21`) holds the canonical stored value.
- `src/config/backend/EdFi.DmsConfigurationService.Backend.Mssql/OpenIddict/Repositories/OpenIddictDataRepository.cs:275-285`
  — `GetApplicationByClientIdAsync` matches `a.ClientId = @ClientId`, which under SQL Server's
  default case-insensitive collation authenticates the client regardless of the casing supplied.

**What's wrong**

The two combine so that one registered client can mint tokens whose `client_id` claim differs only
by case. A client that authenticates as `acme-client` on one call and `Acme-Client` on another gets
two tokens bearing different claim values, even though both are the same stored client.

Consequence for the change in this branch: `OpenIddictTokenManager.RevokeTokenAsync` compares the
target token's `client_id` to the caller's with `StringComparison.Ordinal`, so the holder of the
`Acme-Client` token cannot revoke the `acme-client` token. Per RFC 7009 the caller still receives
`200 OK`, so the failure is silent and visible only in a Debug-level log line.

**Why this deserves priority beyond its apparent rarity:** the silent failure lands squarely on an
incident-response path. Someone revoking a leaked credential sees `200 OK`, concludes the token is
dead, and stops responding — while the token stays valid until it expires on its own. The defect
converts a containment action into false confidence, which is worse than an outright error would
be. Until it is fixed, revocation must be confirmed with `POST /connect/introspect`
(`{"active": false}`), as documented in `CS-AUTH.md` and `docs/OWASP-AUTH-COVERAGE.md`.

**Why it is out of scope**

- The defect is in the **minting path**, which predates this branch and which
  `tasks/plan.md` does not touch. The ownership check merely surfaces it.
- The `Ordinal` comparison in `RevokeTokenAsync` is the correct, security-conservative choice and
  **must stay**. Loosening it to a case-insensitive comparison would paper over the real bug and
  would make the ownership boundary depend on the collation of whichever database engine is
  deployed — Postgres is case-sensitive, SQL Server is not by default — so the same token would be
  revocable on one engine and not the other.
- The proper fix is to mint the claim from `applicationInfo.ClientId` so the claim always carries
  the stored canonical value. That is a change to token issuance, with its own compatibility
  question for tokens already in circulation, and belongs in its own ticket.

**Suggested follow-up:** separate ticket to mint `client_id` from `applicationInfo.ClientId`, and to
decide whether client-id lookup should be case-sensitive consistently across both database engines.
