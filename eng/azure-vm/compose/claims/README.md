# Claim sets

A claim set is the authorization profile attached to an API client (key/secret) when you
create an Application in the Configuration Service. It controls which resources the client
can touch and under which authorization strategy.

## Built-in (embedded) claim sets

Available out of the box (`DMS_CONFIG_CLAIMS_SOURCE=Embedded`). Confirm the live list with
`GET /<config>/v3/claimSets`:

| Claim set | Authorization |
|-----------|---------------|
| `E2E-NoFurtherAuthRequiredClaimSet` | Full access (no scoping) — broad testing |
| `E2E-NameSpaceBasedClaimSet` | Namespace-based (vendor `namespacePrefixes`) |
| `E2E-RelationshipsWithEdOrgsOnlyClaimSet` | **EdOrg relationships** — school/district-level access |
| `E2E-RelationshipsWithEdOrgsOnlyInvertedClaimSet` / `...OrInverted...` / `...MixedStrategy...` | EdOrg variants |

Extension claim sets (`SampleExtensionClaims`, `HomographExtensionClaims`) are **not** embedded —
they ship only as filesystem fragments, so they are available only via **Hybrid** mode (below).

## School / district-level access

Use `E2E-RelationshipsWithEdOrgsOnlyClaimSet` and bind the Application to specific
`educationOrganizationIds` (e.g. district `255901` = Grand Bend ISD, or a school like
`255901001`). The client then only sees data for those EdOrgs and their descendants.
Create such a client via the Configuration Service when you need to demonstrate it. The
default `bootstrap.ps1` provisions only the single-tenant + two-tenant apps (the review scope).

## Creating custom claim sets

Two ways:

1. **API (recommended, no restart):** the Configuration Service exposes full CRUD —
   `POST /v3/claimSets`, `PUT/DELETE /v3/claimSets/{id}`, `POST /v3/claimSets/copy`,
   `POST /v3/claimSets/import`, and `GET /v3/claimSets/{id}/export`. Easiest path:
   `export` an existing claim set as a template, edit, then `import`/`POST` it. Use a CMS
   admin token (see `http/multi-tenant.http`).

2. **File-based:** set `DMS_CONFIG_CLAIMS_SOURCE=Hybrid` in `.env` and drop custom claim set
   fragments in this directory (mounted into both Config Services at `/app/additional-claims`).
   Each file must be named `*-claimset.json` and hold `{ "name": "<ClaimSetName>",
   "resourceClaims": [ ... ] }` — the fragment shape used by the built-in fragments under
   `src/config/backend/EdFi.DmsConfigurationService.Backend/Deploy/AdditionalClaimsets/`. This is
   **not** the API `export`/`import` shape (which uses `claimSetName`), so copy a fragment rather
   than an API export. Restart the `*-config` services (or use the management reload).

> Keep this directory free of partial/invalid fragments while in Hybrid mode — a malformed
> claim set can fail CMS startup. Files not named `*-claimset.json` (like this README) are ignored.
